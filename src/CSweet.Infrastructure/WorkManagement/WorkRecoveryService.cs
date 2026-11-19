using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Current facts about the failed owner's operation. A read never grants execution or changes work.</summary>
public sealed class WorkRecoveryService(CSweetDbContext db, IPersonalTodoService todos,
    IScopedActionAuthorizationService authorization, WorkIssueService issues, IEnumerable<IWorkRecoveryAuthorizationPolicy>? policies = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<Wire.WorkRecoveryFacts> ReadAsync(Guid org, PersonalTodoActor actor, Wire.ReadWorkRecoveryRequest request, CancellationToken ct)
    {
        var directory = await todos.ListAsync(org, actor, cancellationToken: ct);
        var view = directory.Boards.SingleOrDefault(b => b.Items.Any(w => w.Id == request.ItemId))
            ?? throw new UnauthorizedAccessException("Recovery requires current access to the assigned work.");
        var item = view.Items.Single(w => w.Id == request.ItemId);
        return (await AssessVisibleItemAsync(org, item, ct)).Facts;
    }

    internal async Task<(Wire.WorkRecoveryFacts Facts, bool UpdatedPackage)> AssessVisibleItemAsync(
        Guid org, Wire.PersonalTodoItem item, CancellationToken ct)
    {
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(u => u.Id == item.OwnerOrganizationUserId && u.OrganizationId == org && u.IsActive && u.ArchivedAt == null, ct);
        var failure = await db.WorkItemActivities.AsNoTracking().Where(a => a.OrganizationId == org && a.WorkItemId == item.Id && a.EventType == "agent.ticket.failed")
            .OrderByDescending(a => a.OccurredAt).FirstOrDefaultAsync(ct);
        using var data = JsonDocument.Parse(failure?.DataJson ?? "{}");
        string? Field(string name) => data.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var code = Field("failureCode"); var capability = Field("failedCapability");
        var currentFailureStop = data.RootElement.TryGetProperty("blockedRevision", out var blocked) && blocked.ValueKind == JsonValueKind.Number && blocked.GetInt64() == item.Revision;
        if (code is null && owner.AgentInstallationId is Guid installation)
        {
            // Legacy feedback retained machine diagnostics in the execution inbox, not its human-facing sentence.
            var executionInstallation = failure?.ActorKind == CSweet.Domain.Security.GrantSubjectKind.AgentInstallation ? failure.ActorSubjectId : installation;
            var stableLineage = executionInstallation == installation || await db.AgentInstallations.AnyAsync(current => current.Id == installation &&
                db.AgentInstallations.Any(old => old.Id == executionInstallation && old.InstallationKey == current.InstallationKey), ct);
            var prefix = $"personal-todo-available:{item.Id:N}:";
            var legacy = await db.AgentWorkItems.AsNoTracking().Where(w => w.AgentInstallationId == executionInstallation && w.OrganizationId == org.ToString() && w.IdempotencyKey.StartsWith(prefix))
                .OrderByDescending(w => w.CreatedAt).Select(w => w.LastError).FirstOrDefaultAsync(ct);
            code = AgentTicketFeedback.DiagnosticField(legacy ?? "", "code");
            capability = AgentTicketFeedback.DiagnosticField(legacy ?? "", "capability");
            currentFailureStop = stableLineage && (currentFailureStop ||
                legacy is not null && item.BlockReason == AgentTicketFeedback.RepeatedIssuePrefix + AgentTicketFeedback.FailureSentence(legacy));
        }
        Guid? boardId = item.WorkContext?.BoardId;
        if (boardId is null && item.WorkContext?.WorkstreamId is Guid project)
        {
            var boards = await db.WorkBoards.AsNoTracking().Where(b => b.OrganizationId == org && b.WorkstreamId == project &&
                b.ManagerOrganizationUserId == owner.Id && b.ArchivedAt == null && (item.WorkContext.TeamId == null || b.TeamId == item.WorkContext.TeamId))
                .Select(b => b.Id).Take(2).ToArrayAsync(ct);
            if (boards.Length == 1) boardId = boards[0];
        }
        var ownerBinding = await db.AgentInstallations.AsNoTracking().Where(i => i.Id == owner.AgentInstallationId)
            .Select(i => new { i.Id, i.PackageVersionId, i.RevisionNumber, i.IsEnabled, i.RevisionStatus, i.SetupState, grantRevision = i.Grant == null ? (long?)null : i.Grant.GrantRevision }).SingleOrDefaultAsync(ct);
        string? continuation = null;
        var eligibility = "Unknown"; var explanation = "The failed operation needs an owned assessment; no retry authority is inferred.";
        string? operationBasis = null;
        var updatedPackage = false;
        if (currentFailureStop && item.Status == Wire.PersonalTodoStatuses.Blocked && code?.EndsWith("denied", StringComparison.OrdinalIgnoreCase) == true && capability is not null)
        {
            var candidates = (policies ?? []).Append(new BoardPlanningRecoveryPolicy(db, authorization, issues)).Where(p => p.Handles(capability)).ToArray();
            if (candidates.Length == 1)
            {
                var active = await db.AgentInstallations.AsNoTracking().Where(i => i.Id == owner.AgentInstallationId && i.IsEnabled &&
                    i.RevisionStatus == CSweet.Domain.Setup.PluginRevisionStatus.Active && i.SetupState == CSweet.Domain.Setup.PluginSetupState.Ready)
                    .Select(i => i.Grant).SingleOrDefaultAsync(ct);
                var supported = (active is null ? [] : JsonSerializer.Deserialize<string[]>(active.RequiredCapabilitiesJson, Json)!).ToHashSet(StringComparer.Ordinal);
                var check = await candidates[0].EvaluateAsync(new(org, owner.Id, boardId, capability, supported) { WorkItemId = item.Id, OwnerInstallationId = owner.AgentInstallationId, WorkContext = item.WorkContext }, ct);
                operationBasis = check.Basis; continuation = check.ContinuationCapability;
                eligibility = check.Allowed is null ? "Unknown" : active is not null && check.Allowed == true && supported.Contains(continuation ?? capability)
                    ? continuation is null ? "Permitted" : "AlternativeAvailable" : "Denied";
                explanation = eligibility == "Denied" ? "The current installation lacks authority or a supported continuation for the recorded operation. Resolve that owner's current access or intent before retrying." : check.Explanation;
            }
        }
        if (eligibility == "Unknown" && failure is not null && currentFailureStop && item.Status == Wire.PersonalTodoStatuses.Blocked &&
            ownerBinding is { IsEnabled: true, RevisionStatus: CSweet.Domain.Setup.PluginRevisionStatus.Active, SetupState: CSweet.Domain.Setup.PluginSetupState.Ready })
        {
            var replacement = await db.AgentPackageVersions.AsNoTracking()
                .Where(v => v.Id == ownerBinding.PackageVersionId && v.ImportedAt > failure.OccurredAt)
                .Select(v => new { v.Id, v.Version, v.ImportedAt }).SingleOrDefaultAsync(ct);
            if (replacement is not null)
            {
                updatedPackage = true;
                operationBasis = MutationContent.Digest(new { prior = operationBasis, replacement.Id, replacement.ImportedAt });
                explanation += $" The owner's installed package {replacement.Version} was imported at {replacement.ImportedAt:O}, after this task's recorded failure at {failure.OccurredAt:O}. " +
                    "The reporting manager can assess a diagnostic retry against the changed implementation. The outcome on this version is unverified.";
            }
        }
        var basis = MutationContent.Digest(new { policy = "work-recovery.v1", item.Id, owner = owner.Id, owner.AgentInstallationId, ownerBinding,
            fingerprint = Field("fingerprint"), code, capability, boardId, operationBasis, eligibility, continuation,
            item.WorkContext });
        return (new(item.Id, owner.Id, owner.AgentInstallationId, boardId, code ?? "unknown", capability, eligibility, basis, explanation) { FailureEventId = failure?.Id, ContinuationCapability = continuation }, updatedPackage);
    }
}
