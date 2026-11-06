using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Exact managed approvals and resumable host-owned fulfillment share one durable request.</summary>
public sealed class ResourceFulfillmentService(CSweetDbContext db, IEnumerable<IResourceFulfillmentHandler> handlers, TimeProvider clock)
{
    public const string StateKind = "resource-fulfillment.v1";
    public const string CommandKind = "resource-fulfillment-command.v1";
    public const string ActionType = "resource.fulfill.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static ResourceRequestState Read(PluginOperationalState row) => JsonSerializer.Deserialize<ResourceRequestState>(row.PayloadJson, Json)!;
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    public async Task<ResourceRequestState> RequestAsync(Guid organization, Guid installation, ResourceRequest input, CancellationToken token)
    {
        if (input.WorkstreamId == Guid.Empty || string.IsNullOrWhiteSpace(input.Purpose) || input.Purpose.Length > 4000 ||
            string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 160)
            throw new ArgumentException("A project, purpose and stable request key are required.");
        var actor = await ActorAsync(organization, installation, token);
        await RequireProjectAsync(actor, input.WorkstreamId, token);
        var identity = $"request:{actor.Id:N}:{input.IdempotencyKey}";
        var command = JsonSerializer.Serialize(input, Json);
        var previous = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.Kind == CommandKind && x.ExternalKey == identity, token);
        if (previous is not null)
        {
            var receipt = JsonSerializer.Deserialize<RequestReceipt>(previous.PayloadJson, Json)!;
            if (receipt.Command != command) throw new InvalidOperationException("The request key is bound to different requirements.");
            return await GetAsync(organization, receipt.Id, token);
        }
        var handler = handlers.SingleOrDefault(x => x.Supports(input.ResourceType, input.ProfileKey))
            ?? throw new InvalidOperationException("No fulfillment handler supports these requirements. No approval was created.");
        // Preparation may fetch published metadata, but grants no installation or execution authority.
        var plan = await handler.PrepareAsync(organization, input, token);
        var approver = await CurrentManagerAsync(actor, token);
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid(); var approvalId = Guid.NewGuid();
        var state = new ResourceRequestState(id, input.WorkstreamId, actor.Id, approver.Id, approvalId,
            input.ResourceType, input.ProfileKey, input.Purpose, "PendingApproval", 1, Hash(plan), plan, null, null, null, now, now);
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await LockAsync(organization, token);
        var replay = await db.PluginOperationalStates.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization && x.Kind == CommandKind && x.ExternalKey == identity, token);
        if (replay is not null)
        {
            var receipt = JsonSerializer.Deserialize<RequestReceipt>(replay.PayloadJson, Json)!;
            if (receipt.Command != command) throw new InvalidOperationException("The request key is bound to different requirements.");
            return await GetAsync(organization, receipt.Id, token);
        }
        // Coalesce concurrent requests from the same agent/project/profile, including lost replies.
        var candidates = await db.PluginOperationalStates.Where(x => x.OrganizationId == organization && x.Kind == StateKind &&
            x.AgentInstallationId == installation).ToListAsync(token);
        var active = candidates.Select(Read).SingleOrDefault(x => x.WorkstreamId == input.WorkstreamId && x.ResourceType == input.ResourceType &&
            x.ProfileKey == input.ProfileKey && x.Status is "PendingApproval" or "Approved" or "Installing" or "Certifying" or "WaitingDependency" or "Ready");
        if (active is not null) state = active;
        else
        {
            db.PluginOperationalStates.Add(new() { Id = id, OrganizationId = organization, AgentInstallationId = installation,
                Kind = StateKind, ExternalKey = id.ToString("N"), Revision = 1, CreatedAt = now, UpdatedAt = now,
                PayloadJson = JsonSerializer.Serialize(state, Json) });
            db.ActionProposals.Add(new() { Id = approvalId, OrganizationId = organization, AgentInstallationId = installation,
                ActionType = ActionType, Summary = plan.Summary, PayloadJson = ApprovalBinding(state), RiskClass = "ResourceProvisioning",
                IdempotencyKey = $"resource:{id:N}", CreatedAt = now, Status = ProposalStatus.Pending });
            QueueWake(organization, state, installation, "requested");
        }
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
            Kind = CommandKind, ExternalKey = identity, PayloadJson = JsonSerializer.Serialize(new RequestReceipt(state.Id, command), Json),
            CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return state;
    }

    public async Task<IReadOnlyList<ResourceRequestState>> ReadAsync(Guid organization, Guid installation, ResourceRead query, CancellationToken token)
    {
        var actor = await ActorAsync(organization, installation, token);
        var rows = db.PluginOperationalStates.AsNoTracking().Where(x => x.OrganizationId == organization && x.Kind == StateKind);
        if (query.RequestId is { } id) rows = rows.Where(x => x.Id == id);
        // Filter host-owned canonical JSON before the bounded page, so unrelated recent
        // resources cannot hide an older pending approval or another project's recovery.
        if (query.PendingApprovalOnly)
        {
            var approver = "\"approverId\":\"" + actor.Id.ToString("D") + "\"";
            rows = rows.Where(x => x.PayloadJson.Contains("\"status\":\"PendingApproval\"") && x.PayloadJson.Contains(approver));
        }
        if (query.WorkstreamId is { } requestedProject)
        {
            var projectMarker = "\"workstreamId\":\"" + requestedProject.ToString("D") + "\"";
            rows = rows.Where(x => x.PayloadJson.Contains(projectMarker));
        }
        var current = await rows.OrderByDescending(x => x.UpdatedAt).Take(100).ToListAsync(token);
        var result = new List<ResourceRequestState>();
        foreach (var state in current.Select(Read))
        {
            if (query.WorkstreamId is { } project && state.WorkstreamId != project) continue;
            if (query.PendingApprovalOnly && (state.Status != "PendingApproval" || state.ApproverId != actor.Id)) continue;
            if (state.RequesterId != actor.Id && state.ApproverId != actor.Id && !await ManagesProjectAsync(actor, state.WorkstreamId, token)) continue;
            result.Add(state);
        }
        return result;
    }

    public async Task<ResourceRequestState> DecideAsync(OrganizationUser actor, ResourceDecision decision, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(decision.Comment) || decision.Comment.Length > 4000 ||
            string.IsNullOrWhiteSpace(decision.IdempotencyKey) || decision.IdempotencyKey.Length > 160)
            throw new ArgumentException("A rationale and stable decision key are required.");
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await LockAsync(actor.OrganizationId, token);
        var row = await db.PluginOperationalStates.SingleAsync(x => x.OrganizationId == actor.OrganizationId && x.Kind == StateKind && x.Id == decision.RequestId, token);
        await db.Entry(row).ReloadAsync(token);
        var state = Read(row);
        await RequireApproverAsync(actor, state, token);
        var commandKey = $"decision:{decision.RequestId:N}:{decision.IdempotencyKey}";
        var command = JsonSerializer.Serialize(new { actorId = actor.Id, decision }, Json);
        var previous = await db.PluginOperationalStates.SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId && x.Kind == CommandKind && x.ExternalKey == commandKey, token);
        if (previous is not null)
        {
            if (previous.PayloadJson != command) throw new InvalidOperationException("The decision key is already bound to another command.");
            return state;
        }
        var retry = decision.Decision == "Retry" && state.Status is "Failed" or "NeedsAttention" or "Revoked";
        if (state.Revision != decision.ExpectedRevision || state.PlanHash != decision.PlanHash || state.Status != "PendingApproval" && !retry)
            throw new InvalidOperationException("The resource plan or request changed. Read it again before deciding.");
        var proposal = await db.ActionProposals.SingleAsync(x => x.Id == state.ApprovalId && x.OrganizationId == actor.OrganizationId, token);
        await db.Entry(proposal).ReloadAsync(token);
        // PostgreSQL jsonb normalizes whitespace and member ordering. Compare the exact
        // reviewed values, not their serialized formatting.
        if (!retry && (proposal.Status != ProposalStatus.Pending || !System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(proposal.PayloadJson), System.Text.Json.Nodes.JsonNode.Parse(ApprovalBinding(state)))) ||
            retry && proposal.Status != ProposalStatus.Approved)
            throw new InvalidOperationException("The exact resource approval changed.");
        if (decision.Decision == "Escalate")
        {
            var manager = await CurrentManagerAsync(actor, token);
            if (manager.Id == state.RequesterId) throw new InvalidOperationException("Resource approval cannot cycle to its requester.");
            state = state with { ApproverId = manager.Id, Condition = decision.Comment };
        }
        else
        {
            if (decision.Decision is "Approve" or "Retry")
            {
                var handler = Handler(state);
                await handler.ValidateAsync(actor.OrganizationId, state, token);
                if (state.Plan.Cost != 0) throw new InvalidOperationException("This fulfillment profile supports only verified zero-cost plans.");
            }
            state = state with { Status = decision.Decision switch { "Approve" or "Retry" => "Approved", "Reject" => "Rejected",
                "RequestRevision" => "RevisionRequested", _ => throw new ArgumentException("Unknown resource decision.") }, Condition = decision.Comment };
            if (retry) state = state with { FulfillmentAttempt = state.FulfillmentAttempt + 1, CertificationRunId = null };
            proposal.Status = decision.Decision is "Approve" or "Retry" ? ProposalStatus.Approved : decision.Decision == "Reject" ? ProposalStatus.Rejected : ProposalStatus.Cancelled;
            proposal.DecidedAt = clock.GetUtcNow();
        }
        Save(row, state, decision.Decision.ToLowerInvariant());
        if (decision.Decision == "Escalate") proposal.PayloadJson = ApprovalBinding(Read(row));
        db.PluginOperationalStates.Add(new() { Id = Guid.NewGuid(), OrganizationId = actor.OrganizationId, AgentInstallationId = row.AgentInstallationId,
            Kind = CommandKind, ExternalKey = commandKey, PayloadJson = command, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return Read(row);
    }

    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var candidate = await db.PluginOperationalStates.AsNoTracking().Where(x => x.Kind == StateKind && x.AvailableAt != null && x.AvailableAt <= now)
            .OrderBy(x => x.AvailableAt).FirstOrDefaultAsync(token);
        if (candidate is null) return false;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(token) : null;
        await LockAsync(candidate.OrganizationId, token);
        var row = await db.PluginOperationalStates.SingleAsync(x => x.Id == candidate.Id, token);
        await db.Entry(row).ReloadAsync(token);
        if (row.AvailableAt is null || row.AvailableAt > now) return true;
        var state = Read(row);
        if (state.Status is not ("Approved" or "Installing" or "Certifying" or "WaitingDependency" or "Ready")) { row.AvailableAt = null; await db.SaveChangesAsync(token); return true; }
        try
        {
            var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == row.OrganizationId && x.Id == state.ApproverId, token);
            await RequireApproverAsync(actor, state, token);
            var proposal = await db.ActionProposals.SingleAsync(x => x.Id == state.ApprovalId && x.OrganizationId == row.OrganizationId, token);
            if (proposal.Status != ProposalStatus.Approved) throw new UnauthorizedAccessException("Resource approval was withdrawn.");
            var handler = Handler(state);
            await handler.ValidateAsync(row.OrganizationId, state, token);
            var progress = await handler.ReconcileAsync(row.OrganizationId, state.ResumeStatus is { } resume ? state with { Status = resume } : state, token);
            if (progress.Status is not ("Installing" or "Certifying" or "Ready" or "Failed" or "NeedsAttention"))
                throw new InvalidOperationException("The fulfillment handler returned an unsupported status.");
            var next = state with { Status = progress.Status, Condition = progress.Condition,
                ProviderInstallationId = progress.ProviderInstallationId ?? state.ProviderInstallationId,
                CertificationRunId = progress.CertificationRunId ?? state.CertificationRunId, ResumeStatus = null };
            if (next != state) Save(row, next, progress.Status.ToLowerInvariant());
            row.AvailableAt = progress.Status is "Failed" or "NeedsAttention" ? null : now.AddSeconds(progress.Status == "Ready" ? 60 : 5);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException)
        {
            var waiting = state with { Status = "WaitingDependency", ResumeStatus = state.ResumeStatus ?? state.Status,
                Condition = "Setup is waiting for a temporary provider dependency: " + error.Message };
            if (waiting != state) Save(row, waiting, "waiting");
            row.AvailableAt = now.AddSeconds(15);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or UnauthorizedAccessException or
            CSweet.Application.Setup.AgentInstallationException or CSweet.Application.Setup.AgentBuildException)
        {
            Save(row, state with { Status = state.Status == "Ready" ? "Revoked" : "NeedsAttention", Condition = error.Message }, "blocked");
            row.AvailableAt = null;
        }
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return true;
    }

    public async Task<OrganizationUser> ActorAsync(Guid organization, Guid installation, CancellationToken token) =>
        await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization && x.AgentInstallationId == installation &&
            x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Agent, token)
            ?? throw new UnauthorizedAccessException("An active agent identity in this business is required.");
    public async Task<ResourceRequestState> GetAsync(Guid organization, Guid id, CancellationToken token) =>
        Read(await db.PluginOperationalStates.AsNoTracking().SingleAsync(x => x.OrganizationId == organization && x.Kind == StateKind && x.Id == id, token));
    private IResourceFulfillmentHandler Handler(ResourceRequestState state) => handlers.Single(x => x.Key == state.Plan.HandlerKey && x.Supports(state.ResourceType, state.ProfileKey));
    private async Task<OrganizationUser> CurrentManagerAsync(OrganizationUser actor, CancellationToken token)
    {
        var configuration = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == actor.AgentInstallationId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(token);
        var requiresOwner = !ManagedActionApprovalAuthority.RequiresManager(configuration);
        var seen = new HashSet<Guid> { actor.Id };
        while (actor.ReportsToOrganizationUserId is { } parent && seen.Add(parent))
        {
            actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId &&
                x.Id == parent && x.IsActive && x.ArchivedAt == null, token)
                ?? throw new InvalidOperationException("The current reporting chain is unavailable.");
            if (actor.EmployeeType == EmployeeType.Human && actor.ApplicationUserId.HasValue && (!requiresOwner || actor.PermissionLevel == OrganizationPermissionLevel.Owner)) return actor;
            if (requiresOwner) continue;
            if (actor.EmployeeType != EmployeeType.Agent) continue;
            var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x =>
                x.Id == actor.AgentInstallationId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
            var grants = JsonSerializer.Deserialize<string[]>(installation?.Grant?.RequiredCapabilitiesJson ?? "[]", Json) ?? [];
            if (grants.Contains(ResourceCapabilities.Decide, StringComparer.Ordinal)) return actor;
        }
        throw new InvalidOperationException("No active approver on the reporting chain has resource decision authority.");
    }
    private async Task RequireApproverAsync(OrganizationUser actor, ResourceRequestState state, CancellationToken token)
    {
        if (!actor.IsActive || actor.ArchivedAt is not null || actor.Id != state.ApproverId) throw new UnauthorizedAccessException("Only the current assigned approver can decide or fulfill this resource.");
        if (actor.EmployeeType == EmployeeType.Agent)
        {
            var managerInstallation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant).SingleOrDefaultAsync(x =>
                x.Id == actor.AgentInstallationId && x.BusinessId == actor.OrganizationId.ToString("D") && x.IsEnabled &&
                x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
            var grants = JsonSerializer.Deserialize<string[]>(managerInstallation?.Grant?.RequiredCapabilitiesJson ?? "[]", Json) ?? [];
            if (!grants.Contains(ResourceCapabilities.Decide, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("The manager's resource approval authority is no longer granted.");
        }
        var requester = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == actor.OrganizationId && x.Id == state.RequesterId && x.IsActive && x.ArchivedAt == null, token);
        var requesterConfiguration = await db.AgentInstallationConfigurations.AsNoTracking().Where(x => x.AgentInstallationId == requester.AgentInstallationId)
            .Select(x => x.SettingsJson).SingleOrDefaultAsync(token);
        if (!ManagedActionApprovalAuthority.RequiresManager(requesterConfiguration) &&
            (actor.EmployeeType != EmployeeType.Human || actor.PermissionLevel != OrganizationPermissionLevel.Owner))
            throw new UnauthorizedAccessException("The requester currently requires owner approval.");
        // Escalations must stay on the live reporting chain; a prior managerial relationship grants nothing.
        var originalRequester = requester;
        var seen = new HashSet<Guid> { requester.Id };
        while (requester.ReportsToOrganizationUserId is { } manager && seen.Add(manager))
        {
            if (manager == actor.Id) { await RequireProjectAsync(originalRequester, state.WorkstreamId, token); return; }
            requester = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == actor.OrganizationId && x.Id == manager && x.IsActive && x.ArchivedAt == null, token)
                ?? throw new UnauthorizedAccessException("The reporting chain changed.");
        }
        throw new UnauthorizedAccessException("The approval is outside the current reporting chain.");
    }
    private async Task RequireProjectAsync(OrganizationUser actor, Guid project, CancellationToken token)
    {
        if (!await db.Workstreams.AnyAsync(x => x.Id == project && x.OrganizationId == actor.OrganizationId && (x.Status == WorkstreamStatus.Active || x.Status == WorkstreamStatus.Approved), token)) throw new UnauthorizedAccessException("The project is outside this business.");
        var installation = await db.AgentInstallations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.AgentInstallationId && x.BusinessId == actor.OrganizationId.ToString("D") &&
            x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token);
        if (installation is null) throw new UnauthorizedAccessException("The requester installation is no longer active.");
        // Team and manager authority use the same project scope as normal compute access.
        var projectPolicy = new ProjectWorkPolicy(db, clock);
        var board = await db.WorkBoards.Where(x => x.OrganizationId == actor.OrganizationId && x.WorkstreamId == project && x.ArchivedAt == null).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        if (board is null) { if (!await ManagesProjectAsync(actor, project, token)) throw new UnauthorizedAccessException("Project membership is required."); }
        else await projectPolicy.RequireAsync(actor.OrganizationId, actor.Id, board.Value, token);
    }
    private async Task<bool> ManagesProjectAsync(OrganizationUser actor, Guid project, CancellationToken token) =>
        await db.Workstreams.AnyAsync(x => x.OrganizationId == actor.OrganizationId && x.Id == project && x.AccountableManagerOrganizationUserId == actor.Id, token) ||
        await db.WorkstreamSupervisionAssignments.AnyAsync(x => x.OrganizationId == actor.OrganizationId && x.WorkstreamId == project && x.SupervisorOrganizationUserId == actor.Id && x.EndsAt == null, token);
    private async Task LockAsync(Guid organization, CancellationToken token)
    {
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"resource:" + organization}, 0))", token);
    }
    private void Save(PluginOperationalState row, ResourceRequestState value, string reason)
    {
        value = value with { Revision = row.Revision + 1, UpdatedAt = clock.GetUtcNow() };
        row.Revision = value.Revision; row.UpdatedAt = value.UpdatedAt; row.PayloadJson = JsonSerializer.Serialize(value, Json);
        row.AvailableAt = value.Status is "Approved" or "Installing" or "Certifying" or "Ready" ? clock.GetUtcNow() : null;
        QueueWake(row.OrganizationId, value, row.AgentInstallationId, reason);
    }
    private void QueueWake(Guid organization, ResourceRequestState state, Guid requesterInstallation, string reason)
    {
        var now = clock.GetUtcNow();
        db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, TargetInstallationId = requesterInstallation,
            EventType = ResourceCapabilities.Changed, DataJson = JsonSerializer.Serialize(new { requestId = state.Id, workstreamId = state.WorkstreamId,
                revision = state.Revision, reason, approverId = state.ApproverId }, Json),
            IdempotencyKey = $"resource:{state.Id:N}:{state.Revision}:requester", OccurredAt = now, NextAttemptAt = now });
        db.ApplicationRealtimeOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization,
            EventType = CSweet.Contracts.Realtime.AppRealtimeEvents.ResourceChanged,
            Subject = $"organizations/{organization:D}/resources/{state.Id:D}",
            DataJson = JsonSerializer.Serialize(new { requestId = state.Id, workstreamId = state.WorkstreamId, revision = state.Revision }, Json),
            OccurredAt = now, NextAttemptAt = now });
        var supervisors = db.WorkstreamSupervisionAssignments.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.WorkstreamId == state.WorkstreamId && x.EndsAt == null).Select(x => x.SupervisorOrganizationUserId).ToList();
        var leaderInstallations = db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization &&
            supervisors.Contains(x.Id) && x.IsActive && x.ArchivedAt == null && x.Id != state.ApproverId && x.AgentInstallationId != requesterInstallation)
            .Select(x => x.AgentInstallationId).ToList();
        foreach (var leader in leaderInstallations.OfType<Guid>().Distinct())
            db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, TargetInstallationId = leader,
                EventType = ResourceCapabilities.Changed, DataJson = JsonSerializer.Serialize(new { requestId = state.Id, workstreamId = state.WorkstreamId, revision = state.Revision, reason }, Json),
                IdempotencyKey = $"resource:{state.Id:N}:{state.Revision}:leader:{leader:N}", OccurredAt = now, NextAttemptAt = now });
        // Dispatcher uses the request's current approver identity, never a model-supplied installation.
        var approver = db.CoreOrganizationUsers.AsNoTracking().SingleOrDefault(x => x.Id == state.ApproverId && x.OrganizationId == organization && x.IsActive);
        if (approver?.AgentInstallationId is { } target && target != requesterInstallation)
            db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization, TargetInstallationId = target,
                EventType = ResourceCapabilities.Changed, DataJson = JsonSerializer.Serialize(new { requestId = state.Id, workstreamId = state.WorkstreamId, revision = state.Revision, reason }, Json),
                IdempotencyKey = $"resource:{state.Id:N}:{state.Revision}:manager", OccurredAt = now, NextAttemptAt = now });
    }
    public static string ApprovalBinding(ResourceRequestState state) => JsonSerializer.Serialize(new { resourceRequestId = state.Id,
        payloadHash = state.PlanHash, idempotencyKey = $"resource:{state.Id:N}", actionType = ActionType, approverId = state.ApproverId,
        resourceId = state.WorkstreamId.ToString("D"), expectedRevision = state.Revision, plan = state.Plan }, Json);
    private sealed record RequestReceipt(Guid Id, string Command);
}
