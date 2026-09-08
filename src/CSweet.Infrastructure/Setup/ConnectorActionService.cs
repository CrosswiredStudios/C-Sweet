using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>The runtime-facing action surface. Identity and grants always come from authenticated host state.</summary>
public sealed class ConnectorActionService(CSweetDbContext db, ConnectorPlanService plans, ConnectorActionApprovalService approvals,
    IAuditEventWriter? audit = null)
{
    public const string CancellationKind = "host-connector-action-cancellation";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record CancellationReceipt(string IdempotencyKey);

    public async Task<ConnectorAction> RequestAsync(Guid organizationId, Guid requesterId, RequestConnectorAction request, CancellationToken ct)
    {
        await RequireControlAsync(organizationId, requesterId, PlatformCapabilities.ConnectorActionRequest, ct);
        if (string.IsNullOrWhiteSpace(request.Capability) || request.Capability.Length > 200 || request.Input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A declared operation and object input are required.");
        if (request.Input.TryGetProperty("idempotencyKey", out var key) &&
            (key.ValueKind != JsonValueKind.String || key.GetString() != request.IdempotencyKey))
            throw new ArgumentException("The operation and action must use the same idempotency key.");
        var plan = await plans.PrepareAsync(organizationId, requesterId, request.Capability, request.Input, request.IdempotencyKey, ct, request.MediaSource);
        if (plan.ApprovalId is null)
            _ = await approvals.RequestAsync(organizationId, requesterId, plan.Id, plan.PlanHash, ct);
        return await SnapshotAsync(organizationId, requesterId, plan, ct);
    }

    public async Task<ConnectorAction> ReadAsync(Guid organizationId, Guid requesterId, ReadConnectorAction request, CancellationToken ct)
    {
        await RequireControlAsync(organizationId, requesterId, PlatformCapabilities.ConnectorActionRead, ct);
        var plan = await db.ConnectorExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.ApprovalId == request.ActionId &&
            x.OrganizationId == organizationId && x.RequesterInstallationId == requesterId, ct)
            ?? throw new UnauthorizedAccessException("This action does not belong to this installation.");
        return await SnapshotAsync(organizationId, requesterId, plan, ct);
    }

    private async Task<ConnectorAction> SnapshotAsync(Guid organizationId, Guid requesterId, ConnectorExecution plan, CancellationToken ct)
    {
        var status = plan.Status; JsonElement? result = null; string? condition = null;
        if (status != "Cancelled")
        {
            try { _ = await plans.ValidateResultAuthorityAsync(organizationId, requesterId, plan.Id, plan.PlanHash, ct); }
            catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException or JsonException)
            { return new(plan.ApprovalId!.Value, plan.Capability, "Unavailable", plan.UpdatedAt, null, "authority_changed"); }
        }
        if (status is "Prepared" or "AwaitingApproval" or "Approved" && plan.ExpiresAt <= DateTimeOffset.UtcNow)
        { status = "Expired"; condition = "approval_expired"; }
        if (status == "Completed" && plan.ResultJson is { } storedResult)
            result = JsonSerializer.Deserialize<JsonElement>(storedResult);
        if (status == "Indeterminate") condition = "reconciliation_required";
        if (status == "Blocked") condition = "intervention_required";
        ConnectorActionDecision? feedback = null;
        if (status != "Cancelled")
        {
            var key = plan.ApprovalId!.Value.ToString("N");
            var stored = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.AgentInstallationId == requesterId && x.Kind == ConnectorActionApprovalService.ReceiptKind && x.ExternalKey == key, ct);
            if (stored is not null)
            {
                var receipt = JsonSerializer.Deserialize<ConnectorActionApprovalService.DecisionReceipt>(stored.PayloadJson, Json)!;
                var decision = receipt.Status switch { "Approved" => "Approve", "Rejected" => "Reject", "RevisionRequested" => "RequestRevision", _ => null };
                if (receipt.ProposalId != plan.ApprovalId || receipt.PlanHash != plan.PlanHash || decision is null || receipt.Comment?.Length > 4000)
                    throw new InvalidOperationException("The decision feedback does not match this action.");
                feedback = new(decision, receipt.Comment, receipt.DecidedAt);
            }
        }
        return new(plan.ApprovalId!.Value, plan.Capability, status, plan.UpdatedAt, result, condition, feedback);
    }

    public async Task<ConnectorAction> CancelAsync(Guid organizationId, Guid requesterId, CancelConnectorAction request, CancellationToken ct)
    {
        await RequireControlAsync(organizationId, requesterId, PlatformCapabilities.ConnectorActionCancel, ct);
        if (request.ActionId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 160 || request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("An action and bounded stable cancellation key are required.");
        var plan = await db.ConnectorExecutions.SingleOrDefaultAsync(x => x.ApprovalId == request.ActionId &&
            x.OrganizationId == organizationId && x.RequesterInstallationId == requesterId, ct)
            ?? throw new UnauthorizedAccessException("This action does not belong to this installation.");
        var key = request.ActionId.ToString("N");
        var prior = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.Kind == CancellationKind && x.ExternalKey == key, ct);
        if (prior is not null)
        {
            if (JsonSerializer.Deserialize<CancellationReceipt>(prior.PayloadJson, Json)!.IdempotencyKey != request.IdempotencyKey)
                throw new InvalidOperationException("This action already has a different cancellation receipt.");
            return await SnapshotAsync(organizationId, requesterId, plan, ct);
        }
        if (plan.Status == "Cancelled") return await SnapshotAsync(organizationId, requesterId, plan, ct);
        if (plan.Status is not ("AwaitingApproval" or "Approved" or "Blocked" or "Rejected" or "RevisionRequested"))
            throw new InvalidOperationException("Execution has started or its outcome needs reconciliation; cancellation cannot undo it.");
        var proposal = await db.ActionProposals.SingleAsync(x => x.Id == request.ActionId && x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.ActionType == ConnectorActionApprovalService.ActionType, ct);
        var now = DateTimeOffset.UtcNow;
        plan.Status = "Cancelled"; plan.Revision++; plan.UpdatedAt = now;
        if (proposal.Status == ProposalStatus.Pending) { proposal.Status = ProposalStatus.Cancelled; proposal.DecidedAt = now; }
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = requesterId,
            Kind = CancellationKind, ExternalKey = key, PayloadJson = JsonSerializer.Serialize(new CancellationReceipt(request.IdempotencyKey), Json),
            Revision = 1, CreatedAt = now, UpdatedAt = now });
        await approvals.QueueExecutionEventAsync(plan, ct);
        await db.SaveChangesAsync(ct); // The same optimistic revision fences cancellation against the execution claim.
        if (audit is not null) await audit.WriteAsync("connector.action.cancelled", nameof(ConnectorExecution), plan.Id,
            "The requesting installation cancelled its action before external execution.", cancellationToken: ct);
        return await SnapshotAsync(organizationId, requesterId, plan, ct);
    }

    private async Task RequireControlAsync(Guid organizationId, Guid requesterId, string capability, CancellationToken ct)
    {
        var organization = organizationId.ToString("D");
        var grants = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == requesterId && x.BusinessId == organization &&
            x.IsEnabled && x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active)
            .Select(x => x.Grant!.RequiredCapabilitiesJson).SingleOrDefaultAsync(ct);
        if (!(JsonSerializer.Deserialize<string[]>(grants ?? "[]") ?? []).Contains(capability, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("The installation has not been granted this action control.");
    }
}
