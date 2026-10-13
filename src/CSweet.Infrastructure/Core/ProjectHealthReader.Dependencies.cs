using System.Text.Json;
using ProjectDiagnosticEvidence = CSweet.Agent.SDK.ProjectDiagnosticEvidence;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

public sealed partial class ProjectHealthReader
{
    private async Task ObserveDependencyWaitsAsync(Workstream project, WorkTask[] tickets,
        List<AgentCoordinationSession> sessions, List<HealthFailure> failures, HashSet<string> recovered,
        List<ProjectDiagnosticEvidence> evidence, CancellationToken token)
    {
        foreach (var ticket in tickets)
        {
            var key = $"dependency-wait:{ticket.Id:N}";
            if (ticket.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled || string.IsNullOrWhiteSpace(ticket.WaitingReason))
            { recovered.Add(key); continue; }
            if (ticket.ClaimEventId.HasValue) continue;
            Wire.PersonalTodoWorkContext? context;
            try { context = JsonSerializer.Deserialize<Wire.PersonalTodoWorkContext>(ticket.PersonalWorkContextJson ?? "null",
                new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { continue; }
            if (context?.WorkstreamId != project.Id) continue;
            DateTimeOffset? clearedAt = null;
            string? detail = null;
            if (ticket.WaitingReason.Contains("awaiting hiring", StringComparison.OrdinalIgnoreCase) && context.TeamId is { } team)
            {
                // A headcount receipt alone is insufficient: confirm the fulfilled employees
                // are still active, ready and on this project's current team.
                var teamAssigned = await db.WorkstreamTeamAssignments.AsNoTracking().AnyAsync(x =>
                    x.OrganizationId == project.OrganizationId && x.WorkstreamId == project.Id && x.TeamId == team &&
                    x.StartsAt <= clock.GetUtcNow() && x.EndsAt == null, token);
                if (!teamAssigned) continue;
                if (!await db.OrganizationTeams.AsNoTracking().AnyAsync(x => x.Id == team &&
                    x.OrganizationId == project.OrganizationId && x.ArchivedAt == null, token)) continue;
                var plans = await db.WorkforcePlans.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
                    x.WorkstreamId == project.Id && x.TeamId == team && x.RequestingInstallationId == ticket.AssignedAgentInstallationId &&
                    (x.Status == ProposalStatus.Pending || x.Status == ProposalStatus.Approved)).ToListAsync(token);
                if (plans.Count == 0 || plans.Any(x => x.Status != ProposalStatus.Approved || x.Headcount < 1 || x.FulfilledHeadcount < x.Headcount)) continue;
                var planIds = plans.Select(x => x.Id).ToArray();
                var fulfillments = await db.HiringRecommendationFulfillments.AsNoTracking().Where(x =>
                    x.OrganizationId == project.OrganizationId && planIds.Contains(x.WorkforcePlanId)).ToListAsync(token);
                var employees = fulfillments.Select(x => x.ResultOrganizationUserId).Distinct().ToArray();
                var members = await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == project.OrganizationId &&
                    x.TeamId == team && x.EndedAt == null && employees.Contains(x.OrganizationUserId)).ToListAsync(token);
                var ready = await db.CoreOrganizationUsers.AsNoTracking().Include(x => x.AgentInstallation).Where(x =>
                    x.OrganizationId == project.OrganizationId && employees.Contains(x.Id) && x.IsActive && x.ArchivedAt == null &&
                    x.AgentInstallation != null && x.AgentInstallation.IsEnabled &&
                    x.AgentInstallation.RevisionStatus == PluginRevisionStatus.Active && x.AgentInstallation.SetupState == PluginSetupState.Ready &&
                    x.AgentInstallation.ConfigurationSyncStatus == AgentConfigurationSyncStatus.Current).ToListAsync(token);
                if (plans.Any(p => fulfillments.Where(f => f.WorkforcePlanId == p.Id &&
                    members.Any(m => m.OrganizationUserId == f.ResultOrganizationUserId) &&
                    ready.Any(u => u.Id == f.ResultOrganizationUserId)).Select(f => f.ResultOrganizationUserId).Distinct().Count() < p.Headcount)) continue;
                clearedAt = fulfillments.Select(x => x.CreatedAt).Concat(members.Select(x => x.JoinedAt))
                    .Concat(ready.Select(x => x.AgentInstallation!.UpdatedAt)).Append(ticket.CreatedAt).Max();
                detail = "The recorded project hiring requests are fulfilled and their employees are ready on the current team, but planning still reports awaiting hiring.";
            }
            else if (context.DecisionId is { } decisionId)
            {
                var decision = await db.WorkstreamDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == decisionId &&
                    x.OrganizationId == project.OrganizationId && x.WorkstreamId == project.Id, token);
                if (decision is { Status: "Decided" })
                { clearedAt = decision.UpdatedAt; detail = "The exact recorded project decision is decided, but its dependent work remains waiting."; }
            }
            else if (!string.IsNullOrWhiteSpace(context.SourceFingerprint) &&
                ticket.WaitingReason.Contains("planning proposals", StringComparison.OrdinalIgnoreCase))
            {
                var matching = sessions.Where(x => x.SourceBoardId == context.BoardId && context.BoardId.HasValue &&
                    x.TeamId == context.TeamId && x.InitiatorOrganizationUserId == ticket.AssignedEmployeeId &&
                    x.InitiatorInstallationId == ticket.AssignedAgentInstallationId && x.SourceKind == "Board").ToArray();
                var ids = matching.Select(x => x.Id).ToArray();
                var exactCycles = await db.AgentCoordinationTurns.AsNoTracking().Where(x => ids.Contains(x.SessionId) &&
                    x.SpeakerOrganizationUserId == ticket.AssignedEmployeeId && x.ArtifactKey == context.SourceFingerprint)
                    .Select(x => x.SessionId).Distinct().ToArrayAsync(token);
                matching = matching.Where(x => exactCycles.Contains(x.Id) &&
                    (context.WorkItemId == null || x.SourceWorkItemId == null || x.SourceWorkItemId == context.WorkItemId)).ToArray();
                if (matching.Length > 0 && matching.All(x => x.Status == AgentCoordinationStatus.Completed && x.CompletedAt.HasValue))
                { clearedAt = matching.Max(x => x.CompletedAt); detail = "The planning collaborations for this exact source fingerprint completed, but their dependent work remains waiting."; }
            }
            // Permit a bounded wake/reconnect grace period. Repeated deferral or unrelated
            // activity cannot reset it: the dependency's authoritative completion is the anchor.
            if (clearedAt is null || clock.GetUtcNow() - (clearedAt > ticket.CreatedAt ? clearedAt.Value : ticket.CreatedAt) < ProjectHealthService.Threshold) continue;
            var fact = new ProjectDiagnosticEvidence(nameof(WorkTask), ticket.Id, clearedAt.Value, Safe(detail!),
                $"/organizations/{project.OrganizationId}/employees/{ticket.AssignedEmployeeId}?tab=personal-board&item={ticket.Id}");
            failures.Add(new(key, "Planning is waiting on a dependency that already cleared", ticket.Id, [fact]));
            evidence.Add(fact);
        }
    }
}
