using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Connects frozen provider plans to the existing authoritative managed-action decision record.</summary>
public sealed class ConnectorActionApprovalService(CSweetDbContext db, ConnectorPlanService plans, IAuditEventWriter audit)
{
    public const string ActionType = "connector.operation";
    public const string ReceiptKind = "host-connector-action-decision";
    public const string EventKind = "host-connector-action-event";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Binding(Guid PlanId, string PayloadHash, string ChannelId, string ResourceId,
        long ExpectedRevision, string IdempotencyKey, string ActionType, bool AlwaysRequiresApproval,
        Guid ApproverOrganizationUserId, string ApprovalMode, string Effect, DateTimeOffset ExpiresAt,
        JsonElement? ReviewPayload, string? AccountName);
    public sealed record DecisionReceipt(Guid ProposalId, Guid ActorId, string DecisionKey, string DecisionHash,
        string PlanHash, string Status, DateTimeOffset DecidedAt);

    public async Task<ActionProposal> RequestAsync(Guid organizationId, Guid requesterId, Guid planId, string planHash, CancellationToken ct)
    {
        var frozen = await plans.RevalidateAsync(organizationId, requesterId, planId, planHash, ct);
        if (frozen.Request.Effect == "read") throw new InvalidOperationException("Read operations do not need public-action approval.");
        var execution = await db.ConnectorExecutions.SingleAsync(x => x.Id == planId, ct);
        if (execution.ApprovalId is { } existingId)
        {
            var existing = await db.ActionProposals.SingleAsync(x => x.Id == existingId && x.OrganizationId == organizationId, ct);
            _ = RequireBinding(existing, execution);
            return existing;
        }
        if (execution.Status != "Prepared") throw new InvalidOperationException("This plan cannot start another approval.");
        var route = await ResolveApproverAsync(organizationId, requesterId, ct);
        var accountName = await db.PluginConnections.Where(x => x.Id == frozen.ConnectionId)
            .Select(x => x.ExternalAccountName).SingleAsync(ct);
        var binding = new Binding(planId, planHash, frozen.ResourceId, ReviewResource(frozen), execution.Revision,
            frozen.IdempotencyKey, frozen.Capability, true, route.Actor.Id, route.Mode, frozen.Request.Effect,
            execution.ExpiresAt, Review(frozen), accountName);
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = requesterId, ActionType = ActionType,
            Summary = $"Review {frozen.Capability} for {accountName ?? frozen.ResourceId}.",
            PayloadJson = JsonSerializer.Serialize(binding, Json),
            RiskClass = frozen.Request.Effect == "write" ? "PublicMutation" : "AlwaysApproval",
            IdempotencyKey = $"connector-plan:{planId:N}", CreatedAt = DateTimeOffset.UtcNow };
        db.ActionProposals.Add(proposal);
        execution.ApprovalId = proposal.Id; execution.Status = "AwaitingApproval"; execution.Revision++;
        execution.UpdatedAt = DateTimeOffset.UtcNow;
        QueueEvent(proposal, binding, "Requested");
        await db.SaveChangesAsync(ct); // Proposal, plan correlation and notification obligation commit together.
        await audit.WriteAsync("connector.action.proposed", nameof(ActionProposal), proposal.Id,
            "Prepared an exact connector plan for its assigned approver.", cancellationToken: ct);
        return proposal;
    }

    public async Task<string> DecideAsync(Guid organizationId, Guid actorId, DecideManagedAgentActionRequest request, CancellationToken ct)
    {
        if (request.Decision is not ("Approve" or "RequestRevision" or "Reject") ||
            string.IsNullOrWhiteSpace(request.DecisionIdempotencyKey) || request.DecisionIdempotencyKey.Length > 160 ||
            request.Comment?.Length > 4000 || request.Decision == "RequestRevision" && string.IsNullOrWhiteSpace(request.Comment))
            throw new InvalidOperationException("The decision requires a valid choice, stable key and revision feedback when requested.");
        var proposal = await db.ActionProposals.SingleOrDefaultAsync(x => x.Id == request.ProposalId &&
            x.OrganizationId == organizationId && x.ActionType == ActionType, ct)
            ?? throw new UnauthorizedAccessException("This connector action is not available in this organization.");
        var binding = Parse(proposal);
        var execution = await db.ConnectorExecutions.SingleAsync(x => x.Id == binding.PlanId && x.OrganizationId == organizationId, ct);
        _ = RequireBinding(proposal, execution);
        var route = await ResolveApproverAsync(organizationId, proposal.AgentInstallationId, ct);
        if (actorId != binding.ApproverOrganizationUserId || route.Actor.Id != actorId || route.Mode != binding.ApprovalMode)
            throw new UnauthorizedAccessException("Only the currently assigned approver can decide this exact action.");
        if (request.PayloadHash != binding.PayloadHash || request.ExpectedRevision != binding.ExpectedRevision ||
            request.ResourceId != binding.ResourceId || request.ActionIdempotencyKey != binding.IdempotencyKey)
            throw new InvalidOperationException("The reviewed action does not match the frozen plan.");
        var hash = ConnectorRequestMaterializer.Hash(JsonSerializer.SerializeToElement(new { actorId, request }, Json));
        var prior = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == proposal.AgentInstallationId && x.Kind == ReceiptKind && x.ExternalKey == proposal.Id.ToString("N"), ct);
        if (prior is not null)
        {
            var receipt = JsonSerializer.Deserialize<DecisionReceipt>(prior.PayloadJson, Json)!;
            if (receipt.ActorId != actorId || receipt.DecisionKey != request.DecisionIdempotencyKey || receipt.DecisionHash != hash)
                throw new InvalidOperationException("This action already has a different binding decision.");
            return receipt.Status;
        }
        if (proposal.Status != ProposalStatus.Pending || execution.Status != "AwaitingApproval" || binding.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("This decision is stale or the plan has expired.");
        _ = await plans.RevalidateAsync(organizationId, proposal.AgentInstallationId, execution.Id, binding.PayloadHash, ct);
        proposal.Status = request.Decision switch { "Approve" => ProposalStatus.Approved, "Reject" => ProposalStatus.Rejected, _ => ProposalStatus.Cancelled };
        proposal.DecidedAt = DateTimeOffset.UtcNow;
        execution.Status = request.Decision switch { "Approve" => "Approved", "Reject" => "Rejected", _ => "RevisionRequested" };
        execution.Revision++; execution.UpdatedAt = proposal.DecidedAt.Value;
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
            AgentInstallationId = proposal.AgentInstallationId, Kind = ReceiptKind, ExternalKey = proposal.Id.ToString("N"),
            PayloadJson = JsonSerializer.Serialize(new DecisionReceipt(proposal.Id, actorId, request.DecisionIdempotencyKey,
                hash, binding.PayloadHash, execution.Status, proposal.DecidedAt.Value), Json),
            Revision = 1, CreatedAt = proposal.DecidedAt.Value, UpdatedAt = proposal.DecidedAt.Value });
        QueueEvent(proposal, binding, execution.Status);
        await db.SaveChangesAsync(ct); // Optimistic plan revision prevents competing decisions across contexts.
        await audit.WriteAsync("connector.action.decided", nameof(ActionProposal), proposal.Id,
            $"Recorded {request.Decision} for the exact connector plan; provider execution has not occurred.", cancellationToken: ct);
        return execution.Status;
    }

    public async Task<FrozenConnectorPlan> RequireApprovedAsync(Guid organizationId, Guid requesterId, Guid planId, string planHash, CancellationToken ct)
    {
        var frozen = await plans.RevalidateAsync(organizationId, requesterId, planId, planHash, ct);
        var execution = await db.ConnectorExecutions.AsNoTracking().SingleAsync(x => x.Id == planId, ct);
        if (execution.Status is not ("Approved" or "Executing") || execution.ApprovalId is not { } proposalId)
            throw new UnauthorizedAccessException("This plan does not have an executable decision.");
        var proposal = await db.ActionProposals.AsNoTracking().SingleAsync(x => x.Id == proposalId, ct);
        var binding = RequireBinding(proposal, execution);
        var route = await ResolveApproverAsync(organizationId, requesterId, ct);
        if (proposal.Status != ProposalStatus.Approved || route.Actor.Id != binding.ApproverOrganizationUserId || route.Mode != binding.ApprovalMode)
            throw new UnauthorizedAccessException("The action's approval authority has changed.");
        var receipt = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.Kind == ReceiptKind && x.ExternalKey == proposalId.ToString("N"), ct);
        var decision = receipt is null ? null : JsonSerializer.Deserialize<DecisionReceipt>(receipt.PayloadJson, Json);
        if (decision is null || decision.Status != "Approved" || decision.PlanHash != planHash || decision.ActorId != route.Actor.Id)
            throw new UnauthorizedAccessException("The exact action's decision receipt is missing or invalid.");
        return frozen;
    }

    public static Binding Parse(ActionProposal proposal) =>
        JsonSerializer.Deserialize<Binding>(proposal.PayloadJson, Json) ?? throw new InvalidOperationException("The connector approval binding is invalid.");

    private static Binding RequireBinding(ActionProposal proposal, ConnectorExecution execution)
    {
        var binding = Parse(proposal);
        using var storedPlan = JsonDocument.Parse(execution.PlanJson);
        if (ConnectorRequestMaterializer.Hash(storedPlan.RootElement) != execution.PlanHash)
            throw new UnauthorizedAccessException("The stored request plan was modified.");
        var frozen = storedPlan.RootElement.Deserialize<FrozenConnectorPlan>(Json)!;
        JsonElement? preview = Review(frozen);
        if (proposal.ActionType != ActionType || proposal.OrganizationId != execution.OrganizationId ||
            proposal.AgentInstallationId != execution.RequesterInstallationId || execution.ApprovalId != proposal.Id ||
            binding.PlanId != execution.Id || binding.PayloadHash != execution.PlanHash || binding.ChannelId != execution.ResourceId ||
            binding.ResourceId != ReviewResource(frozen) || binding.IdempotencyKey != execution.IdempotencyKey || binding.ActionType != execution.Capability ||
            binding.ExpiresAt != execution.ExpiresAt || binding.Effect != frozen.Request.Effect || !binding.AlwaysRequiresApproval ||
            binding.ReviewPayload.HasValue != preview.HasValue ||
            preview.HasValue && !JsonElement.DeepEquals(preview.Value, binding.ReviewPayload!.Value))
            throw new UnauthorizedAccessException("The approval and execution records do not describe the same plan.");
        return binding;
    }

    private static string ReviewResource(FrozenConnectorPlan plan) => plan.Request.ResourceChecks.Count == 1
        ? plan.Request.ResourceChecks[0].ResourceId : plan.ResourceId;

    // Include query-only deletions, every resource and media digest as well as body changes.
    // This is a native, escaped review projection, never executable connector-provided UI.
    private static JsonElement Review(FrozenConnectorPlan plan) => JsonSerializer.SerializeToElement(new
    {
        account = plan.ResourceId,
        resources = plan.Request.ResourceChecks.Select(x => x.ResourceId).Distinct().ToArray(),
        method = plan.Request.Method,
        destination = plan.Request.Url,
        changes = plan.Request.Body is { } body ? JsonSerializer.Deserialize<JsonElement>(body) : (JsonElement?)null,
        media = plan.Media
    }, Json);

    private async Task<(OrganizationUser Actor, string Mode)> ResolveApproverAsync(Guid organizationId, Guid requesterId, CancellationToken ct)
    {
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.AgentInstallationId == requesterId && x.IsActive, ct) ?? throw new UnauthorizedAccessException("The requesting employee is inactive.");
        var settings = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == requesterId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        using var configuration = JsonDocument.Parse(settings ?? "{}");
        var mode = configuration.RootElement.TryGetProperty("approvalMode", out var value) ? value.GetString() : "Manager Approval";
        if (mode is not ("Manager Approval" or "CEO Approval" or "Fully Autonomous"))
            throw new UnauthorizedAccessException("The approval policy is invalid.");
        if (mode == "Manager Approval" && requester.ReportsToOrganizationUserId is { } manager)
        {
            var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == manager &&
                x.OrganizationId == organizationId && x.IsActive && x.Id != requester.Id, ct);
            if (actor is not null) return (actor, mode);
        }
        var ceos = await db.CoreOrganizationUsers.AsNoTracking().Include(x => x.Role).Where(x => x.OrganizationId == organizationId &&
            x.IsActive && x.Role!.Name == "CEO" && x.Id != requester.Id).ToArrayAsync(ct);
        if (ceos.Length != 1) throw new UnauthorizedAccessException("One active accountable CEO is required to approve this action.");
        // Autonomous execution is deliberately not inferred from a preference. Until a validated
        // owner-approved standing policy authorizes this exact plan, it routes to the CEO.
        return (ceos[0], mode!);
    }

    public async Task QueueExecutionEventAsync(ConnectorExecution execution, CancellationToken ct)
    {
        if (execution.ApprovalId is not { } proposalId) throw new InvalidOperationException("An action approval correlation is required.");
        var proposal = await db.ActionProposals.SingleAsync(x => x.Id == proposalId && x.OrganizationId == execution.OrganizationId, ct);
        QueueEvent(proposal, Parse(proposal), execution.Status);
    }

    private void QueueEvent(ActionProposal proposal, Binding binding, string status)
    {
        var now = DateTimeOffset.UtcNow;
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId,
            AgentInstallationId = proposal.AgentInstallationId, Kind = EventKind, ExternalKey = $"{proposal.Id:N}:{status}",
            PayloadJson = JsonSerializer.Serialize(new { proposalId = proposal.Id, planId = binding.PlanId, planHash = binding.PayloadHash,
                approverOrganizationUserId = binding.ApproverOrganizationUserId, requesterInstallationId = proposal.AgentInstallationId, status }, Json),
            Revision = 1, CreatedAt = now, UpdatedAt = now });
    }
}
