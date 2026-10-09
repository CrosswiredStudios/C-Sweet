using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

/// <summary>Current-state project review, reporting-chain escalation and exact-command execution.</summary>
public sealed class ProjectApprovalGovernance(CSweetDbContext db)
{
    public const string ReadCapability = "platform.project-approval.read.v1";
    public const string DecideCapability = "platform.project-approval.decide.v1";
    public const string RequestedEvent = "com.csweet.project-approval.requested.v1";
    public const string DecidedEvent = "com.csweet.project-approval.decided.v1";
    private const string RouteKind = "project-approval-route";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Route(Guid ApproverId, string Reason, Guid EscalatedBy);
    public sealed record SpendingPolicy(string Mode, decimal? MaximumAmount, string? Currency);
    public sealed record Review(Guid ProposalId, string Status, Guid RequesterId, Guid? ApproverId,
        JsonElement Binding, SpendingPolicy Spending, ProjectApprovalReader.Receipt? Decision, Route? Escalation);
    public sealed record Decision(Guid ProposalId, string DecisionKind, string Comment, string PayloadHash,
        string ActionIdempotencyKey, string DecisionIdempotencyKey);

    public async Task<Guid?> ApproverAsync(ActionProposal proposal, CancellationToken ct = default)
    {
        var route = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == proposal.OrganizationId && x.Kind == RouteKind && x.ExternalKey == proposal.Id.ToString("N"), ct);
        if (route is not null) return JsonSerializer.Deserialize<Route>(route.PayloadJson, Json)!.ApproverId;
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == proposal.OrganizationId && x.AgentInstallationId == proposal.AgentInstallationId && x.IsActive, ct);
        var settings = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == proposal.AgentInstallationId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        if (ManagedActionApprovalAuthority.RequiresManager(settings)) return requester?.ReportsToOrganizationUserId;
        return null; // Owner approval retains the existing owner policy.
    }

    public async Task<bool> CanDecideAsync(ActionProposal proposal, OrganizationUser actor, CancellationToken ct = default)
    {
        if (!actor.IsActive || actor.ArchivedAt.HasValue || actor.OrganizationId != proposal.OrganizationId) return false;
        var approver = await ApproverAsync(proposal, ct);
        if (approver.HasValue) return approver == actor.Id;
        var settings = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == proposal.AgentInstallationId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        return !ManagedActionApprovalAuthority.RequiresManager(settings) && actor.PermissionLevel == OrganizationPermissionLevel.Owner;
    }

    public async Task<SpendingPolicy> SpendingAsync(OrganizationUser actor, CancellationToken ct = default)
    {
        var settings = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == actor.AgentInstallationId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(ct);
        using var config = JsonDocument.Parse(settings ?? "{}");
        var root = config.RootElement;
        var maximum = root.TryGetProperty("maximumProjectBudget", out var value) && value.TryGetDecimal(out var amount) ? amount : (decimal?)null;
        var currency = root.TryGetProperty("projectBudgetCurrency", out var unit) ? unit.GetString() : null;
        return new(maximum is > 0 ? "Limited" : "Unlimited", maximum is > 0 ? maximum : null, currency);
    }

    public async Task<IReadOnlyList<Review>> ReadAsync(OrganizationUser actor, Guid? proposalId, CancellationToken ct = default)
    {
        var installationIds = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId && x.IsActive &&
            (x.Id == actor.Id || x.ReportsToOrganizationUserId == actor.Id)).Select(x => x.AgentInstallationId).ToListAsync(ct);
        var routes = await db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId && x.Kind == RouteKind).ToListAsync(ct);
        var routedIds = routes.Where(x => JsonSerializer.Deserialize<Route>(x.PayloadJson, Json)!.ApproverId == actor.Id).Select(x => Guid.ParseExact(x.ExternalKey, "N")).ToArray();
        var query = db.ActionProposals.AsNoTracking().Where(x => x.OrganizationId == actor.OrganizationId && x.ActionType == ProjectApprovalReader.ActionType);
        if (actor.PermissionLevel == OrganizationPermissionLevel.Owner)
        {
            var ownerConfigurations = await db.AgentInstallationConfigurations.AsNoTracking().Where(x =>
                db.CoreOrganizationUsers.Any(u => u.OrganizationId == actor.OrganizationId && u.AgentInstallationId == x.AgentInstallationId && u.IsActive))
                .ToListAsync(ct);
            installationIds.AddRange(ownerConfigurations.Where(x => !ManagedActionApprovalAuthority.RequiresManager(x.SettingsJson)).Select(x => (Guid?)x.AgentInstallationId));
        }
        if (!proposalId.HasValue)
            query = query.Where(x => installationIds.Contains(x.AgentInstallationId) || routedIds.Contains(x.Id));
        query = proposalId.HasValue ? query.Where(x => x.Id == proposalId) : query.Where(x => x.Status == ProposalStatus.Pending);
        var proposals = await query.OrderBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var result = new List<Review>();
        foreach (var proposal in proposals)
        {
            var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId &&
                x.AgentInstallationId == proposal.AgentInstallationId && x.IsActive, ct);
            if (requester is null || requester.Id != actor.Id && !await CanDecideAsync(proposal, actor, ct)) continue;
            using var binding = JsonDocument.Parse(proposal.PayloadJson);
            var receipt = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == ProjectApprovalReader.ReceiptKind &&
                x.OrganizationId == actor.OrganizationId && x.ExternalKey == proposal.Id.ToString("N"), ct);
            result.Add(new(proposal.Id, proposal.Status.ToString(), requester.Id, await ApproverAsync(proposal, ct), binding.RootElement.Clone(),
                await SpendingAsync(actor, ct), receipt is null ? null : JsonSerializer.Deserialize<ProjectApprovalReader.Receipt>(receipt.PayloadJson, Json), routes.Where(x => x.ExternalKey == proposal.Id.ToString("N")).Select(x => JsonSerializer.Deserialize<Route>(x.PayloadJson, Json)).SingleOrDefault()));
        }
        return result;
    }

    public async Task DecideAsync(OrganizationUser actor, Decision input, IEnumerable<IManagedActionExecutor> executors, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Comment) || input.Comment.Length > 4000 || string.IsNullOrWhiteSpace(input.DecisionIdempotencyKey) || input.DecisionIdempotencyKey.Length > 160)
            throw new InvalidOperationException("A bounded review rationale and decision key are required.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({input.ProposalId.ToString("D")}, 0))", ct);
        var proposal = await db.ActionProposals.SingleOrDefaultAsync(x => x.Id == input.ProposalId && x.OrganizationId == actor.OrganizationId &&
            x.ActionType == ProjectApprovalReader.ActionType, ct) ?? throw new InvalidOperationException("Project proposal unavailable.");
        if (db.Database.IsRelational()) await db.Entry(proposal).ReloadAsync(ct);
        if (!actor.IsActive || actor.ArchivedAt.HasValue) throw new UnauthorizedAccessException("The reviewing employee is no longer active.");
        var commandJson = JsonSerializer.Serialize(new { actorId = actor.Id, input }, Json);
        var priorCommand = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == "project-approval-command" &&
            x.OrganizationId == actor.OrganizationId && x.ExternalKey == input.DecisionIdempotencyKey, ct);
        if (priorCommand is not null)
        {
            if (priorCommand.PayloadJson != commandJson) throw new InvalidOperationException("The decision key is bound to another command.");
            return;
        }
        var isWithdraw = input.DecisionKind == "Withdraw" && proposal.AgentInstallationId == actor.AgentInstallationId;
        if (!isWithdraw && !await CanDecideAsync(proposal, actor, ct)) throw new UnauthorizedAccessException("Only the current assigned approver can decide this proposal.");
        using var binding = JsonDocument.Parse(proposal.PayloadJson);
        var root = binding.RootElement;
        if (root.GetProperty("payloadHash").GetString() != input.PayloadHash || root.GetProperty("idempotencyKey").GetString() != input.ActionIdempotencyKey)
            throw new InvalidOperationException("The proposal changed after review.");
        if (proposal.Status != ProposalStatus.Pending) throw new InvalidOperationException("The project proposal is no longer pending."); // Exact bound replay has no second effect.
        if (input.DecisionKind == "Escalate")
        {
            var manager = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId &&
                x.Id == actor.ReportsToOrganizationUserId && x.IsActive && x.ArchivedAt == null, ct)
                ?? throw new InvalidOperationException("No active reporting manager is available for escalation.");
            var visited = new HashSet<Guid> { actor.Id };
            OrganizationUser? ancestor = manager;
            while (ancestor is not null)
            {
                if (!visited.Add(ancestor.Id) || visited.Count > 100) throw new InvalidOperationException("The reporting hierarchy contains a cycle or exceeds the escalation bound.");
                ancestor = ancestor.ReportsToOrganizationUserId.HasValue ? await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.OrganizationId == actor.OrganizationId && x.Id == ancestor.ReportsToOrganizationUserId && x.IsActive, ct) : null;
            }
            var route = await db.PluginOperationalStates.SingleOrDefaultAsync(x => x.Kind == RouteKind && x.OrganizationId == actor.OrganizationId &&
                x.ExternalKey == proposal.Id.ToString("N"), ct);
            if (route is null) { route = new() { Id = Guid.NewGuid(), OrganizationId = actor.OrganizationId, AgentInstallationId = proposal.AgentInstallationId,
                Kind = RouteKind, ExternalKey = proposal.Id.ToString("N"), CreatedAt = DateTimeOffset.UtcNow }; db.PluginOperationalStates.Add(route); }
            route.PayloadJson = JsonSerializer.Serialize(new Route(manager.Id, input.Comment, actor.Id), Json);
            route.Revision++; route.UpdatedAt = DateTimeOffset.UtcNow;
            // Routing changes must also wake the human inbox through the same-save realtime outbox.
            proposal.Summary = $"Project approval escalated to {manager.DisplayName}: {input.Comment}";
            QueueWake(proposal, manager.AgentInstallationId, RequestedEvent, $"escalated:{route.Revision}");
        }
        else
        {
            ManagedActionExecutionResult? execution = null;
            if (input.DecisionKind == "Approve")
            {
                var plan = root.GetProperty("payload").Deserialize<W.WorkstreamPlanProposalV2Request>(Json)!;
                var spending = await SpendingAsync(actor, ct);
                if (actor.EmployeeType == EmployeeType.Agent && spending.MaximumAmount.HasValue &&
                    (!plan.ProposedBudgetAmount.HasValue || plan.ProposedBudgetAmount > spending.MaximumAmount ||
                    !string.Equals(plan.ProposedBudgetCurrency, spending.Currency, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The budget exceeds or cannot be verified against delegated spending authority. Escalate the proposal.");
                var executor = executors.SingleOrDefault(x => x.CanExecute(proposal.ActionType)) ?? throw new InvalidOperationException("Project executor unavailable.");
                execution = await executor.ExecuteAsync(proposal, actor, ct);
                proposal.Status = ProposalStatus.Approved;
            }
            else proposal.Status = input.DecisionKind switch { "RequestRevision" or "Withdraw" => ProposalStatus.Cancelled, "Reject" => ProposalStatus.Rejected,
                _ => throw new InvalidOperationException("Unknown project decision.") };
            proposal.DecidedAt = DateTimeOffset.UtcNow;
            db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = actor.OrganizationId, AgentInstallationId = proposal.AgentInstallationId,
                Kind = ProjectApprovalReader.ReceiptKind, ExternalKey = proposal.Id.ToString("N"), Revision = 1, CreatedAt = proposal.DecidedAt.Value,
                UpdatedAt = proposal.DecidedAt.Value, PayloadJson = JsonSerializer.Serialize(new ProjectApprovalReader.Receipt(input.DecisionKind, input.Comment, actor.Id, actor.DisplayName, execution?.ResourceId), Json) });
            var suggestions = await db.SuggestedUserActions.Where(x => x.OrganizationId == actor.OrganizationId && x.OriginatingInstallationId == proposal.AgentInstallationId &&
                x.WorkflowType == CSweet.Contracts.Communications.SuggestedUserActionWorkflows.ReviewApproval).ToListAsync(ct);
            foreach (var suggestion in suggestions.Where(x => CSweet.Infrastructure.Communications.ApprovalUserActionWorkflowResolver.ReadId(x.ParametersJson) == proposal.Id))
            { suggestion.Status = CSweet.Contracts.Communications.SuggestedUserActionStatuses.Completed; suggestion.CompletedAt = proposal.DecidedAt; }
            QueueWake(proposal, proposal.AgentInstallationId, DecidedEvent, "decided");
            if (actor.AgentInstallationId.HasValue && actor.AgentInstallationId != proposal.AgentInstallationId)
                QueueWake(proposal, actor.AgentInstallationId, DecidedEvent, "manager-decided");
        }
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = actor.OrganizationId, AgentInstallationId = proposal.AgentInstallationId,
            Kind = "project-approval-command", ExternalKey = input.DecisionIdempotencyKey, PayloadJson = commandJson, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public void QueueWake(ActionProposal proposal, Guid? target, string eventType, string suffix)
    {
        if (!target.HasValue) return;
        var now = DateTimeOffset.UtcNow;
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = proposal.OrganizationId, TargetInstallationId = target,
            EventType = eventType, DataJson = JsonSerializer.Serialize(new { proposalId = proposal.Id }, Json),
            IdempotencyKey = $"project-approval:{proposal.Id:N}:{suffix}", OccurredAt = now, NextAttemptAt = now });
    }
}
