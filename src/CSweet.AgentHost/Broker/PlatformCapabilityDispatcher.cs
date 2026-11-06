using CSweet.Agent.SDK;
using CSweet.Application.Agents;
using CSweet.Application.Security;
using CSweet.Contracts.Agents;
using CSweet.Infrastructure.Setup;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CSweet.AgentHost.Broker;

public interface IPlatformCapabilityDispatcher
{
    /// <summary>
    /// Decides deny / queue / allow for one capability without running its handler. Every entry point
    /// that can execute a capability must call this first, including paths that route to a provider
    /// outside the handler pipeline. Such a path passes <paramref name="route"/>: no platform handler
    /// is required, and the route's risk class replaces the catalog classification.
    /// </summary>
    Task<CapabilityPreflight> PreflightAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken,
        CapabilityRoute? route = null);

    /// <summary>Runs an already-admitted capability and settles its approval receipt.</summary>
    IAsyncEnumerable<CapabilityResult> ExecuteAsync(
        AgentSession session,
        RequestCapability request,
        CapabilityAdmission? admission,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles the approval receipt for a result produced outside the handler pipeline, so a
    /// provider-routed call is recorded exactly like a handler call.
    /// </summary>
    Task<CapabilityResult> CompleteAsync(
        CapabilityAdmission? admission,
        CapabilityResult result,
        CancellationToken cancellationToken);

    IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A capability that executes outside the platform handler pipeline (a remote MCP server, a
/// connector or another agent installation), with the risk class the gate must apply to it.
/// </summary>
public sealed record CapabilityRoute(CapabilityRiskClass Risk);

/// <summary>
/// The outcome of the admission gate. <see cref="Terminal"/> is what the caller must return;
/// <see cref="Admission"/> is a claimed approval that must be completed once the call finishes.
/// </summary>
public sealed record CapabilityPreflight(IReadOnlyList<CapabilityResult>? Terminal, CapabilityAdmission? Admission)
{
    public static CapabilityPreflight Refuse(CapabilityResult result) => new([result], null);
    public static CapabilityPreflight RefuseAll(IReadOnlyList<CapabilityResult> results) => new(results, null);
    public static CapabilityPreflight Allow(CapabilityAdmission? admission = null) => new(null, admission);
    public bool IsTerminal => Terminal is not null;
}

public sealed class PlatformCapabilityDispatcher(
    IEnumerable<IPlatformCapabilityHandler> handlers,
    IAgentPostureReader postures,
    IAgentCapabilityApprovalService approvals,
    IAgentPolicyService temporalPolicy,
    PluginSetupAssistancePolicy? setupPolicy = null,
    ProjectCapabilityPolicy? projectPolicy = null,
    PluginAccountDataExposureService? providerData = null) : IPlatformCapabilityDispatcher
{
    private readonly IReadOnlyList<IPlatformCapabilityHandler> _handlers = handlers.ToList();

    public async IAsyncEnumerable<CapabilityResult> InvokeAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var preflight = await PreflightAsync(session, request, cancellationToken);
        if (preflight.Terminal is { } terminal)
        {
            foreach (var result in terminal) yield return result;
            yield break;
        }
        await foreach (var result in ExecuteAsync(session, request, preflight.Admission, cancellationToken))
            yield return result;
    }

    public async Task<CapabilityPreflight> PreflightAsync(
        AgentSession session,
        RequestCapability request,
        CancellationToken cancellationToken,
        CapabilityRoute? route = null)
    {
        if (setupPolicy is not null && !McpToolCatalog.SelfReadCapabilities.Contains(request.Capability))
        {
            try
            {
                if (!Guid.TryParse(session.InstallationId, out var installationId))
                    throw new UnauthorizedAccessException("The installation identity is invalid.");
                using var document = JsonDocument.Parse(request.Payload.IsEmpty ? "{}" : System.Text.Encoding.UTF8.GetString(request.Payload.Span));
                await setupPolicy.ValidateCapabilityAsync(session.BusinessId, installationId,
                    request.Capability, document.RootElement, cancellationToken);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or JsonException or InvalidOperationException)
            { return CapabilityPreflight.Refuse(Failure(request.RequestId, "The installation may not invoke this capability in its current setup context.")); }
        }
        if (projectPolicy is not null)
        {
            try
            {
                if (!Guid.TryParse(session.BusinessId, out var org) || !Guid.TryParse(session.InstallationId, out var agent)) throw new UnauthorizedAccessException("Invalid agent identity.");
                await projectPolicy.ValidateAsync(org, agent, request.Capability, request.Payload.ToElement(), cancellationToken);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or JsonException)
            { return CapabilityPreflight.Refuse(Failure(request.RequestId, e.Message)); }
        }
        var requested = session.Grant.RequestedCapabilities ?? new HashSet<string>(StringComparer.Ordinal);
        if (!IsImplicitPlatformCapability(request.Capability) && !requested.Contains(request.Capability))
            return CapabilityPreflight.Refuse(Failure(request.RequestId,
                $"Agent '{session.AgentId}' may not request '{request.Capability}'."));

        // Resolved before the gate so an unexecutable capability never creates an approval row.
        // A routed capability is executed by its provider, so it needs no platform handler.
        if (route is null && !_handlers.Any(candidate => candidate.CanHandle(request.Capability)))
            return CapabilityPreflight.Refuse(Failure(request.RequestId, $"No platform handler provides '{request.Capability}'."));

        var risk = route?.Risk ?? CapabilityRiskCatalog.Classify(request.Capability);
        if (!Guid.TryParse(session.InstallationId, out var installation))
            return CapabilityPreflight.Refuse(Failure(request.RequestId, "The installation identity is invalid."));

        // A readable posture is not permission to run: a disabled or rolled-back installation keeps
        // its stored posture, so the lifecycle state is checked on every dispatch.
        if (!await postures.IsActiveAsync(installation, cancellationToken))
            return CapabilityPreflight.Refuse(Failure(request.RequestId,
                "The agent installation is disabled or no longer on an active revision."));

        var assignment = await postures.GetAsync(installation, cancellationToken);
        if (assignment.IsRefused(risk))
            return CapabilityPreflight.Refuse(Failure(request.RequestId, $"Plan posture refuses '{request.Capability}'."));

        CapabilityAdmission? admission = null;
        if (assignment.RequiresApproval(risk))
        {
            Guid.TryParse(session.BusinessId, out var organization);
            try
            {
                admission = await approvals.AdmitAsync(organization, installation, request.Capability,
                    request.Payload.Span.ToArray(), session.Grant.Revision, risk, cancellationToken);
            }
            catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Microsoft.EntityFrameworkCore.DbUpdateException)
            { return CapabilityPreflight.Refuse(Failure(request.RequestId, "The approval request could not be admitted. Refresh this agent's authority and retry.")); }
            if (admission is null)
                return CapabilityPreflight.Refuse(Failure(request.RequestId, "Approval admission is unavailable."));
            if (admission.ResultsJson is not null)
            {
                var stored = JsonSerializer.Deserialize<StoredCapabilityResult[]>(admission.ResultsJson) ?? [];
                return CapabilityPreflight.RefuseAll(stored.Select(result => new CapabilityResult
                {
                    RequestId = request.RequestId, Succeeded = result.Succeeded,
                    ContentType = result.ContentType ?? "application/json", Error = result.Error,
                    Payload = JsonPayload.From(result.Payload)
                }).ToArray());
            }
            if (admission.Status != "Execute")
            {
                return CapabilityPreflight.Refuse(new CapabilityResult
                {
                    RequestId = request.RequestId, Succeeded = false, ContentType = "application/json",
                    Error = $"Capability approval is {admission.Status}. Retry this exact request after approval; unconfirmed executions are never repeated automatically.",
                    Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new { isError = true, approvalId = admission.ApprovalId,
                        state = admission.Status, retryAfterApproval = admission.Status is "Pending" or "Approved" }))
                });
            }
        }

        // Every admitted destructive/outward execution consumes a durable, tenant-scoped window.
        // The policy service serializes count+record across hosts before returning permission.
        if (risk is CapabilityRiskClass.Destructive or CapabilityRiskClass.Outward)
        {
            var decision = await temporalPolicy.TryAdmitAsync(new AgentPolicyThreshold(risk.ToString(), TimeSpan.FromHours(1),
                risk == CapabilityRiskClass.Destructive ? 3 : 60),
                new AgentPolicyEvent(risk.ToString(), installation, session.BusinessId, risk, DateTimeOffset.UtcNow), cancellationToken);
            if (!decision.Allowed)
            {
                if (admission is not null) await approvals.DeferAsync(admission.ApprovalId, cancellationToken);
                return CapabilityPreflight.Refuse(Failure(request.RequestId, decision.Reason));
            }
        }

        return CapabilityPreflight.Allow(admission);
    }

    public async IAsyncEnumerable<CapabilityResult> ExecuteAsync(
        AgentSession session,
        RequestCapability request,
        CapabilityAdmission? admission,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var handler = _handlers.FirstOrDefault(candidate => candidate.CanHandle(request.Capability));
        if (handler is null)
        { yield return Failure(request.RequestId, $"No platform handler provides '{request.Capability}'."); yield break; }

        var approvedResults = admission is null ? null : new List<StoredCapabilityResult>();
        var receiptBytes = 0;
        var settled = false;
        try
        {
        await foreach (var result in handler.HandleAsync(session, request, cancellationToken))
        {
            string? retentionFailure = null;
            if (providerData != null && result.Succeeded && Guid.TryParse(session.BusinessId, out var org) && Guid.TryParse(session.InstallationId, out var consumer) &&
                (string.IsNullOrEmpty(result.ContentType) || result.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            {
                try { await providerData.RecordResponseAsync(org, consumer, result.Payload.Span.ToArray(), cancellationToken); }
                catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or JsonException or Microsoft.EntityFrameworkCore.DbUpdateException)
                { retentionFailure = "The authorized data response is no longer available or exceeds its safe retention boundary. Refresh and request a smaller page."; }
            }
            var effective = retentionFailure is not null ? Failure(request.RequestId, retentionFailure) : result;
            if (approvedResults is null) yield return effective;
            else
            {
                receiptBytes += effective.Payload.Length;
                if (receiptBytes > 1024 * 1024) throw new InvalidOperationException("The execution result exceeds its durable receipt budget. Reconcile this operation before retrying.");
                approvedResults.Add(Store(effective));
            }
        }
        if (approvedResults is not null)
        {
            if (approvedResults.Count == 0) approvedResults.Add(Store(Failure(request.RequestId, "The handler returned no result.")));
            await approvals.CompleteAsync(admission!.ApprovalId, JsonSerializer.Serialize(approvedResults),
                approvedResults.All(x => x.Succeeded), cancellationToken);
            settled = true;
            foreach (var stored in approvedResults)
                yield return new CapabilityResult { RequestId = request.RequestId, Succeeded = stored.Succeeded,
                    ContentType = stored.ContentType ?? "application/json", Error = stored.Error, Payload = JsonPayload.From(stored.Payload) };
        }
        }
        finally
        {
            // A handler that throws, or an enumeration the caller abandons mid-stream, leaves the
            // approval unconfirmed. It must not stay Executing, or the same operation can never be
            // reviewed again. A settlement failure never masks the original exception.
            if (approvedResults is not null && !settled && admission is not null)
                await TrySettleFailureAsync(admission, request.RequestId);
        }
    }

    private async Task TrySettleFailureAsync(CapabilityAdmission admission, string requestId)
    {
        try
        {
            await approvals.CompleteAsync(admission.ApprovalId,
                JsonSerializer.Serialize(new[] { Store(Failure(requestId, "The capability did not return a confirmed result.")) }),
                false, CancellationToken.None);
        }
        catch (Exception) { /* the row remains recoverable through the abandon path */ }
    }

    public async Task<CapabilityResult> CompleteAsync(
        CapabilityAdmission? admission,
        CapabilityResult result,
        CancellationToken cancellationToken)
    {
        if (admission is null) return result;
        var stored = new[] { Store(result) };
        await approvals.CompleteAsync(admission.ApprovalId, JsonSerializer.Serialize(stored),
            result.Succeeded, cancellationToken);
        return result;
    }

    private static StoredCapabilityResult Store(CapabilityResult result) =>
        new(result.Succeeded, result.ContentType, result.Error, result.Payload.Span.ToArray());

    private static bool IsImplicitPlatformCapability(string capability) =>
        capability is CSweet.Contracts.GenAi.GenAiCapabilities.JobRead or CSweet.Contracts.GenAi.GenAiCapabilities.JobCancel ||
        capability == AgentLifecycleCapabilities.CompleteOnboarding || McpToolCatalog.SelfReadCapabilities.Contains(capability);

    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = error,
        Payload = JsonPayload.FromUtf8("{\"isError\":true}")
    };
}
