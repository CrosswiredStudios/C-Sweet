using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Core;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

/// <summary>One current authorized presentation for each causal issue. Retry permission is not a human assignment.</summary>
public sealed class OverviewIssueReader(CSweetDbContext db, IPersonalTodoService todos, BlockedWorkAttentionReader blocked,
    IExecutiveDecisionService decisions, ProjectHealthService health, WorkIssueService issues)
{
    public async Task<OverviewIssueDirectory> ReadAsync(Guid org, PersonalTodoActor actor, bool history, CancellationToken ct)
    {
        var directory = await todos.ListAsync(org, actor, cancellationToken: ct);
        var work = await blocked.ReadVisibleAsync(org, actor, directory, ct);
        var questions = await decisions.ListPendingForUserAsync(org, actor.OrganizationUserId, ct);
        var incidents = await health.ReadAttentionAsync(org, actor.OrganizationUserId, ct, groupPlanningReports: false);
        var tasks = directory.Boards.SelectMany(b => b.Items).DistinctBy(i => i.Id).ToDictionary(i => i.Id);
        var allCases = await db.WorkIssueCases.AsNoTracking().Where(c => c.OrganizationId == org && (history || c.Status == "Open")).ToListAsync(ct);
        var accessible = new List<CSweet.Domain.WorkManagement.WorkIssueCase>();
        foreach (var group in allCases.GroupBy(c => c.BoardId))
        {
            try {
                _ = await issues.RequireBoardAsync(org, actor.OrganizationUserId, group.Key, false, ct);
                foreach (var row in group) if (await issues.CanReadSourceAsync(org, actor.OrganizationUserId, WorkIssueService.Read<Wire.WorkApprovedSource>(row.SourceJson), ct)) accessible.Add(row);
            }
            catch (UnauthorizedAccessException) { }
        }
        var projects = await db.Workstreams.AsNoTracking().Where(p => p.OrganizationId == org).Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(p => p.OrganizationId == org && p.IsActive).Select(p => new { p.Id, p.DisplayName }).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
        var groupFor = new Dictionary<(string Kind, Guid Id), string>();
        var groups = new Dictionary<string, Builder>();
        var linkedWork = accessible.SelectMany(c => WorkIssueService.Read<Wire.WorkIssueLink[]>(c.LinksJson)
            .Where(l => l.Kind == "WorkItem").Select(l => l.Id)).Distinct().ToArray();
        var boardItems = await db.CoreWorkTasks.AsNoTracking().Where(t => t.OrganizationId == org && linkedWork.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.BoardId }).ToDictionaryAsync(t => t.Id, ct);
        foreach (var c in accessible)
        {
            var key = "case:" + c.Id.ToString("N");
            groups[key] = new(key, c.Id, c.WorkstreamId, projects.GetValueOrDefault(c.WorkstreamId, "Project"),
                string.IsNullOrWhiteSpace(c.BriefTitle) ? "Work under review" : c.BriefTitle,
                string.IsNullOrWhiteSpace(c.BriefSummary) ? c.Statement : c.BriefSummary,
                people.GetValueOrDefault(c.AccountableEmployeeId, "Accountable manager"), c.NextAction,
                c.Statement + "\n\n" + c.Detail, c.Status, c.AccountableEmployeeId);
            if (c.Status == "Open") groupFor[("Issue", c.Id)] = key;
            foreach (var link in WorkIssueService.Read<Wire.WorkIssueLink[]>(c.LinksJson))
            {
                if (c.Status == "Open") groupFor[(link.Kind, link.Id)] = key;
                if (link.Kind == "WorkItem" && boardItems.TryGetValue(link.Id, out var item) && item.BoardId == c.BoardId)
                    groups[key].AffectedWork.Add(new(item.Id, item.Title, $"/organizations/{org}/projects/{c.WorkstreamId}?tab=work&board={c.BoardId}&item={item.Id}"));
            }
        }
        var aliases = new Dictionary<string, string>();
        string Resolve(string key)
        {
            while (aliases.TryGetValue(key, out var parent)) key = parent;
            return key;
        }
        var singleSources = work.Where(w => w.Blockers.Count == 1).ToDictionary(w => w.ItemId, w => w.Blockers[0].ItemId);
        string WorkKey(Guid id)
        {
            string Source(Guid workId) => BlockedWorkAttentionReader.SourceKey(workId, tasks,
                (kind, source) => groupFor.GetValueOrDefault((kind, source)));
            var key = Source(id);
            if (key.StartsWith("work:", StringComparison.Ordinal) && singleSources.TryGetValue(id, out var target)) key = Source(target);
            return Resolve(key);
        }
        Builder Get(string key, Guid? project, string owner, string title, string summary, string next, string detail, Guid? ownerId = null)
        {
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new(key, null, project, project is Guid p ? projects.GetValueOrDefault(p, "Project") : "Business work", title, summary, owner, next, detail, ownerId: ownerId);
            return g;
        }
        // Bind reports before projecting any rows, so work, its recovery task and the report share the same key.
        foreach (var report in incidents)
        {
            var workKey = report.AffectedWorkItemId is Guid affected ? WorkKey(affected) : null;
            var sessionKey = report.BlockedCoordinationId is Guid session
                ? Resolve(groupFor.GetValueOrDefault(("Coordination", session), "coordination:" + session.ToString("N"))) : null;
            var caseKey = groupFor.GetValueOrDefault(("Incident", report.Id));
            var key = caseKey ?? (workKey?.StartsWith("case:", StringComparison.Ordinal) == true ? workKey : sessionKey)
                ?? workKey ?? "incident:" + report.Id.ToString("N");
            foreach (var alias in new[] { workKey, sessionKey })
            {
                // A single task can have two independent blockers. Only a case can tie distinct decisions or sessions together.
                if (alias is not null && alias != key && (alias.StartsWith("work:", StringComparison.Ordinal) ||
                    key.StartsWith("case:", StringComparison.Ordinal) && !alias.StartsWith("case:", StringComparison.Ordinal)))
                    aliases[alias] = key;
            }
            groupFor[("Incident", report.Id)] = key;
        }
        foreach (var item in work.OrderBy(w => tasks.TryGetValue(w.ItemId, out var t) && CSweet.Agent.SDK.ManagerWorkRecovery.IsCommitment(t)))
        {
            var task = tasks.GetValueOrDefault(item.ItemId);
            var direct = WorkKey(item.ItemId);
            foreach (var source in item.Blockers)
            {
                var key = direct.StartsWith("case:", StringComparison.Ordinal) ? direct : WorkKey(source.ItemId);
                var blocker = tasks.GetValueOrDefault(source.ItemId);
                var g = Get(key, blocker?.WorkContext?.WorkstreamId ?? task?.WorkContext?.WorkstreamId, item.OwnerName, source.Title,
                    blocker?.WorkContext?.CoordinationSessionId is not null ? "Waiting for a reply from the team." :
                    blocker?.WorkContext?.DecisionId is not null ? "Waiting for a decision." : "", "", item.Reason, item.OwnerOrganizationUserId);
                g.Work.Add(item); g.Requires |= item.CanRetry;
                g.Unclassified |= task?.WorkContext is null;
            }
        }
        foreach (var incident in incidents)
        {
            var key = Resolve(groupFor[("Incident", incident.Id)]);
            var g = Get(key, incident.WorkstreamId, incident.ReporterName, incident.Reason, "",
                incident.IsStatusReport ? "No response requested; the accountable manager owns recovery." : incident.RecommendedAction, incident.OwnerReport ?? incident.Facts,
                incident.ReporterOrganizationUserId ?? incident.ReporterEmployeeId);
            if (incident.AffectedWorkItemId is Guid affected && tasks.TryGetValue(affected, out var task))
                g.AffectedWork.Add(new(task.Id, task.Title, $"/organizations/{org}/employees/{task.OwnerOrganizationUserId}?tab=personal-board&item={task.Id}"));
            g.Incidents.Add(incident); g.Requires |= incident.HasUserAction;
        }
        var executive = await db.ExecutiveDecisions.AsNoTracking().Where(d => d.OrganizationId == org && questions.Select(q => q.Decision.Id).Contains(d.Id)).ToListAsync(ct);
        foreach (var q in questions)
        {
            Guid? decisionId = null;
            var raw = executive.SingleOrDefault(e => e.Id == q.Decision.Id);
            if (raw is not null)
            {
                using var data = JsonDocument.Parse(raw.OptionsJson);
                // Standalone questions have no project link. Legacy options can also be an array.
                if (data.RootElement.ValueKind == JsonValueKind.Object &&
                    data.RootElement.TryGetProperty("workstreamDecisionId", out var link) &&
                    link.ValueKind == JsonValueKind.String && link.TryGetGuid(out var id))
                    decisionId = id;
            }
            var key = q.Decision.IncidentId is Guid incident ? groupFor.GetValueOrDefault(("Incident", incident)) : null;
            if (key is null && decisionId is Guid source)
                key = groupFor.GetValueOrDefault(("Decision", source), "decision:" + source.ToString("N"));
            if (key is null && work.FirstOrDefault(w => w.DecisionId == q.Decision.Id) is { } linkedWorkItem)
                key = WorkKey(linkedWorkItem.ItemId);
            key = Resolve(key ?? "question:" + q.Decision.Id.ToString("N"));
            var g = Get(key, null, q.AgentName, "Review a project decision", $"{q.AgentName} needs your direction on the recorded question.",
                "Choose a current option or open the supporting evidence before responding.", q.Decision.Prompt, q.AgentOrganizationUserId);
            g.Questions.Add(q); g.Requires = true;
        }
        foreach (var session in await ReadFailedCollaborationsAsync(org, actor.OrganizationUserId, ct))
        {
            var key = Resolve(groupFor.GetValueOrDefault(("Coordination", session.Id), "coordination:" + session.Id.ToString("N")));
            var group = Get(key, session.WorkstreamId, people.GetValueOrDefault(session.InitiatorOrganizationUserId, "Agent collaboration"),
                session.Subject, "", "", "", session.InitiatorOrganizationUserId);
            group.Requires = true;
            group.CollaborationFailureSummary ??= AgentWorkFailure.DescribeCollaborationFailure(session.FinalSummary);
            group.Actions.Add(new(session.Id, "Review collaboration",
                $"/organizations/{org:D}/communications/{session.ConversationId:D}?perspective={session.InitiatorOrganizationUserId:D}&collaboration={session.Id:D}"));
        }
        var reviewReassignments = await db.UserNotifications.AsNoTracking().Where(n => n.OrganizationId == org &&
            n.RecipientOrganizationUserId == actor.OrganizationUserId && n.DismissedAt == null && n.ActionUri != null &&
            n.Category == ArtifactReviewJobLifecycle.ReassignmentCategory &&
            db.CoreOrganizationUsers.Any(u => u.Id == n.RecipientOrganizationUserId && u.OrganizationId == org &&
                u.IsActive && u.ArchivedAt == null && u.EmployeeType == EmployeeType.Human && u.PermissionLevel >= OrganizationPermissionLevel.Manager))
            .Select(n => new { n.Id, n.Title, n.Body, n.ActionUri }).ToListAsync(ct);
        foreach (var notice in reviewReassignments)
        {
            var group = Get("document-review:" + notice.Id.ToString("N"), null, "Documents", notice.Title, notice.Body, "", "");
            group.Requires = true;
            group.Actions.Add(new(notice.Id, "Open document", notice.ActionUri!));
        }
        // Also list visible work tracking this blocker before or after its own blocked state.
        foreach (var task in tasks.Values.Where(t => t.ArchivedAt is null))
        {
            if (groups.TryGetValue(WorkKey(task.Id), out var group))
                group.AffectedWork.Add(new(task.Id, task.Title,
                    $"/organizations/{org}/employees/{task.OwnerOrganizationUserId}?tab=personal-board&item={task.Id}"));
        }
        return OverviewIssueDirectory.Consolidate(groups.Values.Select(g => g.Build()).ToArray());
    }

    private async Task<List<AgentCoordinationSession>> ReadFailedCollaborationsAsync(Guid org, Guid userId, CancellationToken ct)
    {
        var managers = db.CoreOrganizationUsers.AsNoTracking().Where(u => u.OrganizationId == org && u.IsActive &&
            u.ArchivedAt == null && u.EmployeeType == EmployeeType.Human && u.PermissionLevel >= OrganizationPermissionLevel.Manager);
        var viewer = await managers.Where(u => u.Id == userId).Select(u => (OrganizationPermissionLevel?)u.PermissionLevel)
            .SingleOrDefaultAsync(ct);
        if (viewer is null) return [];

        var managerMemberships = db.ConversationParticipants.Where(p => p.LeftAt == null && managers.Any(u => u.Id == p.OrganizationUserId));
        // Use the same audience as execution-failure notifications, re-evaluated against current membership.
        return await db.AgentCoordinationSessions.AsNoTracking().Where(s => s.OrganizationId == org &&
            s.Status == AgentCoordinationStatus.Failed &&
            (managerMemberships.Any(p => p.ConversationId == s.SourceConversationId && p.OrganizationUserId == userId) ||
             viewer == OrganizationPermissionLevel.Owner && !managerMemberships.Any(p => p.ConversationId == s.SourceConversationId)))
            .ToListAsync(ct);
    }

    private sealed class Builder(string key, Guid? id, Guid? project, string projectName, string title, string summary, string owner, string next, string detail, string status = "Open", Guid? ownerId = null)
    {
        public List<OverviewAffectedWork> AffectedWork { get; } = [];
        public List<BlockedWorkAttentionResponse> Work { get; } = [];
        public List<PendingAgentQuestionResponse> Questions { get; } = [];
        public List<ProjectIncidentAttentionResponse> Incidents { get; } = [];
        public List<OverviewIssueAction> Actions { get; } = [];
        public string? CollaborationFailureSummary { get; set; }
        public bool Requires { get; set; }
        public bool Unclassified { get; set; }
        public OverviewIssueCard Build() => new(key, id, project, projectName, title, CollaborationFailureSummary ?? summary,
            owner, next, Requires, Unclassified, Work, Questions, Incidents,
            string.Join("\n\n", new[] { detail }.Concat(Questions.Select(q => q.Decision.Prompt)).Concat(Work.Select(w => w.Reason)).Distinct()), status)
        {
            OwnerOrganizationUserId = ownerId,
            AffectedWork = AffectedWork.DistinctBy(w => w.ItemId).ToArray(),
            Actions = Actions
        };
    }
}
