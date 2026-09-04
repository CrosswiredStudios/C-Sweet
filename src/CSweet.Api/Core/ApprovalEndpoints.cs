using CSweet.Api.Auth;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using CSweet.Application.Setup;

namespace CSweet.Api.Core;

public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/core/organizations/{organizationId:guid}/infrastructure/checkout-actions/{actionId:guid}",
            async (Guid organizationId, Guid actionId, HttpContext http, CSweetDbContext db,
                IPluginSecretStore secrets, CancellationToken cancellationToken) =>
            {
                var applicationUserId = http.User.GetApplicationUserId();
                if (!applicationUserId.HasValue) return Results.Forbid();
                var authorized = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                    x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId.Value &&
                    x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner, cancellationToken);
                if (!authorized) return Results.Forbid();
                var key = actionId.ToString("N");
                var action = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OrganizationId == organizationId && x.Kind == "infrastructure.checkout-action" &&
                    x.ExternalKey == key, cancellationToken);
                if (action is null) return Results.NotFound();
                using var payload = JsonDocument.Parse(action.PayloadJson);
                if (!payload.RootElement.TryGetProperty("expiresAt", out var expiryNode) ||
                    !expiryNode.TryGetDateTimeOffset(out var expiry) || expiry <= DateTimeOffset.UtcNow)
                    return Results.BadRequest(new { error = "checkout_action_expired" });
                var value = await secrets.GetAsync(action.AgentInstallationId,
                    $"infrastructure.checkout-action.{key}", cancellationToken);
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !(uri.Host.Equals("namecheap.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.EndsWith(".namecheap.com", StringComparison.OrdinalIgnoreCase)))
                    return Results.BadRequest(new { error = "checkout_action_invalid" });
                return Results.Redirect(uri.AbsoluteUri);
            });
        endpoints.MapGet(
            "/api/core/organizations/{organizationId:guid}/approvals",
            async (
                Guid organizationId,
                HttpContext http,
                IApprovalDashboardService service,
                CancellationToken cancellationToken) =>
            {
                var applicationUserId = http.User.GetApplicationUserId();
                if (!applicationUserId.HasValue) return Results.Forbid();
                try
                {
                    return Results.Ok(await service.GetAsync(
                        organizationId,
                        applicationUserId.Value,
                        cancellationToken));
                }
                catch (UnauthorizedAccessException exception)
                {
                    return Results.Json(
                        new { error = "approval_access_denied", message = exception.Message },
                        statusCode: StatusCodes.Status403Forbidden);
                }
            });
        endpoints.MapPost(
            "/api/core/organizations/{organizationId:guid}/approvals/agent-actions/{proposalId:guid}/decide",
            async (Guid organizationId, Guid proposalId, DecideManagedAgentActionRequest request,
                HttpContext http, CSweetDbContext db, IAuditEventWriter audit,
                IEnumerable<IManagedActionExecutor> executors, CancellationToken cancellationToken) =>
            {
                var applicationUserId = http.User.GetApplicationUserId();
                if (!applicationUserId.HasValue || request.ProposalId != proposalId) return Results.Forbid();
                var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OrganizationId == organizationId && x.ApplicationUserId == applicationUserId.Value && x.IsActive,
                    cancellationToken);
                var proposal = await db.ActionProposals.SingleOrDefaultAsync(x =>
                    x.Id == proposalId && x.OrganizationId == organizationId, cancellationToken);
                if (actor is null || proposal is null) return Results.NotFound();
                var agent = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OrganizationId == organizationId && x.AgentInstallationId == proposal.AgentInstallationId && x.IsActive,
                    cancellationToken);
                var configurationJson = await db.AgentInstallationConfigurations.AsNoTracking()
                    .Where(x => x.AgentInstallationId == proposal.AgentInstallationId)
                    .Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken);
                var approvalMode = ReadApprovalMode(configurationJson);
                var authorized = approvalMode == "Manager Approval"
                    ? agent?.ReportsToOrganizationUserId == actor.Id
                    : actor.PermissionLevel == OrganizationPermissionLevel.Owner;
                if (!authorized) return Results.Forbid();
                if (proposal.Status != ProposalStatus.Pending)
                    return Results.Conflict(new { error = "stale_decision", message = "The action is no longer pending." });
                if (request.Decision is not (ResourceChangeDecisionKinds.Approve or ResourceChangeDecisionKinds.RequestRevision or ResourceChangeDecisionKinds.Reject) ||
                    string.IsNullOrWhiteSpace(request.DecisionIdempotencyKey) || request.DecisionIdempotencyKey.Length > 160)
                    return Results.BadRequest(new { error = "invalid_decision" });
                if (request.Decision == ResourceChangeDecisionKinds.RequestRevision && string.IsNullOrWhiteSpace(request.Comment))
                    return Results.BadRequest(new { error = "feedback_required" });
                using var stored = JsonDocument.Parse(proposal.PayloadJson);
                var root = stored.RootElement;
                if (root.TryGetProperty("change", out var boundChange) &&
                    boundChange.TryGetProperty("expiresAt", out var expiresNode) &&
                    expiresNode.TryGetDateTimeOffset(out var expiresAt) && expiresAt <= DateTimeOffset.UtcNow)
                {
                    proposal.Status = ProposalStatus.Cancelled;
                    proposal.DecidedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return Results.Conflict(new
                    {
                        error = "approval_expired",
                        message = "The exact infrastructure proposal expired. Reconcile current provider state and create a new proposal."
                    });
                }
                var payloadHash = root.GetProperty("payloadHash").GetString();
                var actionKey = root.GetProperty("idempotencyKey").GetString();
                var resourceId = root.TryGetProperty("resourceId", out var resourceNode) &&
                    resourceNode.ValueKind == JsonValueKind.String ? resourceNode.GetString() : null;
                var revision = root.TryGetProperty("expectedRevision", out var revisionNode) && revisionNode.ValueKind == JsonValueKind.Number
                    ? revisionNode.GetInt64() : (long?)null;
                if (!string.Equals(payloadHash, request.PayloadHash, StringComparison.Ordinal) ||
                    !string.Equals(actionKey, request.ActionIdempotencyKey, StringComparison.Ordinal) ||
                    !string.Equals(resourceId, request.ResourceId, StringComparison.Ordinal) ||
                    revision != request.ExpectedRevision)
                    return Results.Conflict(new { error = "approval_binding_mismatch", message = "The action changed after review." });
                ManagedActionExecutionResult? execution = null;
                if (request.Decision == ResourceChangeDecisionKinds.Approve)
                {
                    var matching = executors.Where(x => x.CanExecute(proposal.ActionType)).ToList();
                    if (matching.Count != 1)
                        return Results.Conflict(new
                        {
                            error = "managed_action_executor_unavailable",
                            message = matching.Count == 0
                                ? $"No executor is registered for '{proposal.ActionType}'. Approval cannot be recorded without executing the bound command."
                                : $"Multiple executors are registered for '{proposal.ActionType}'."
                        });
                    try
                    {
                        execution = await matching[0].ExecuteAsync(proposal, actor, cancellationToken);
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                                       UnauthorizedAccessException or DbUpdateConcurrencyException)
                    {
                        return Results.Conflict(new { error = "managed_action_execution_failed", message = exception.Message });
                    }
                    proposal.Status = ProposalStatus.Approved;
                }
                else
                {
                    proposal.Status = request.Decision == ResourceChangeDecisionKinds.Reject
                        ? ProposalStatus.Rejected
                        : ProposalStatus.Cancelled;
                }
                proposal.DecidedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                if (execution is not null)
                    await audit.WriteAsync($"{proposal.ActionType}.executed", nameof(Workstream), execution.ResourceId,
                        execution.Summary,
                        JsonSerializer.Serialize(new { organizationId, proposalId = proposal.Id, execution.Revision,
                            approver = actor.Id, request.DecisionIdempotencyKey }), cancellationToken);
                await audit.WriteAsync("managed-action.decided", nameof(CSweet.Domain.Core.ActionProposal), proposal.Id,
                    $"{request.Decision} decision recorded for {proposal.ActionType}.",
                    JsonSerializer.Serialize(new { organizationId, proposal.AgentInstallationId, payloadHash,
                        revision, actionKey, request.DecisionIdempotencyKey, actor = actor.Id }), cancellationToken);
                return Results.Ok(new { proposal.Id, status = proposal.Status.ToString(), proposal.DecidedAt, execution });
            });
        return endpoints;
    }

    private static string ReadApprovalMode(string? configurationJson)
    {
        try
        {
            using var configuration = JsonDocument.Parse(configurationJson ?? "{}");
            return configuration.RootElement.TryGetProperty("approvalMode", out var value)
                ? value.GetString() ?? "Manager Approval" : "Manager Approval";
        }
        catch (JsonException) { return "Manager Approval"; }
    }
}
