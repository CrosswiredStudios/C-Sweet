using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.Core;
using CSweet.Contracts.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
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
        var work = await blocked.ReadAsync(org, actor, ct);
        var questions = await decisions.ListPendingForUserAsync(org, actor.OrganizationUserId, ct);
        var incidents = await health.ReadAttentionAsync(org, actor.OrganizationUserId, ct);
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
        foreach (var c in accessible)
        {
            var key = "case:" + c.Id.ToString("N");
            groups[key] = new(key, c.Id, c.WorkstreamId, projects.GetValueOrDefault(c.WorkstreamId, "Project"),
                string.IsNullOrWhiteSpace(c.BriefTitle) ? "Work responsibility under review" : c.BriefTitle,
                string.IsNullOrWhiteSpace(c.BriefSummary) ? "The team is reconciling an approved responsibility." : c.BriefSummary,
                people.GetValueOrDefault(c.AccountableEmployeeId, "Accountable manager"), c.NextAction,
                c.Statement + "\n\n" + c.Detail, c.Status, c.AccountableEmployeeId);
            if (c.Status == "Open") foreach (var link in WorkIssueService.Read<Wire.WorkIssueLink[]>(c.LinksJson)) groupFor[(link.Kind, link.Id)] = key;
        }
        var incidentRows = await db.ProjectIncidents.AsNoTracking().Where(i => i.OrganizationId == org && incidents.Select(v => v.Id).Contains(i.Id))
            .Select(i => new { i.Id, i.Fingerprint, i.AffectedWorkItemId }).ToListAsync(ct);
        string WorkKey(Guid id)
        {
            if (groupFor.TryGetValue(("WorkItem", id), out var value)) return value;
            if (tasks.TryGetValue(id, out var task))
            {
                if (task.WorkContext?.IssueId is Guid issue && groups.ContainsKey("case:" + issue.ToString("N"))) return "case:" + issue.ToString("N");
                if (task.WorkContext?.CoordinationSessionId is Guid session)
                    return groupFor.GetValueOrDefault(("Coordination", session), "coordination:" + session.ToString("N"));
                if (task.WorkContext?.DecisionId is Guid decision) return groupFor.GetValueOrDefault(("Decision", decision), "decision:" + decision.ToString("N"));
                return "work:" + (task.PlanRootId ?? id).ToString("N");
            }
            return "work:" + id.ToString("N");
        }
        Builder Get(string key, Guid? project, string owner, string title, string summary, string next, string detail, Guid? ownerId = null)
        {
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new(key, null, project, project is Guid p ? projects.GetValueOrDefault(p, "Project") : "Business work", title, summary, owner, next, detail, ownerId: ownerId);
            return g;
        }
        foreach (var item in work)
        {
            var task = tasks.GetValueOrDefault(item.ItemId);
            var ownerId = item.ManagerName is null ? item.OwnerOrganizationUserId :
                directory.Boards.FirstOrDefault(b => b.BoardId == task?.BoardId)?.ManagerOrganizationUserId;
            var g = Get(WorkKey(item.ItemId), task?.WorkContext?.WorkstreamId, item.ManagerName ?? item.OwnerName,
                "Review blocked work", $"{item.OwnerName}'s work needs recovery.",
                item.OwnerOrganizationUserId == actor.OrganizationUserId ? "Review the recorded cause before taking the available recovery action." :
                    $"{item.ManagerName ?? item.OwnerName} is accountable for the next recovery step.", item.Reason, ownerId);
            var managerEscalation = task?.CorrelationId?.StartsWith(CSweet.Agent.SDK.ManagerWorkRecovery.Prefix, StringComparison.Ordinal) == true &&
                directory.Boards.Any(b => b.BoardId == task!.BoardId && b.ManagerOrganizationUserId == actor.OrganizationUserId);
            g.Work.Add(item); g.Requires |= item.OwnerOrganizationUserId == actor.OrganizationUserId || managerEscalation;
            g.Unclassified |= task?.WorkContext is null;
        }
        foreach (var incident in incidents)
        {
            var row = incidentRows.Single(i => i.Id == incident.Id);
            var key = groupFor.GetValueOrDefault(("Incident", incident.Id));
            if (key is null && row.AffectedWorkItemId is Guid item) key = WorkKey(item);
            if (key is null && row.Fingerprint.StartsWith("coordination:", StringComparison.Ordinal) && Guid.TryParse(row.Fingerprint[13..], out var session))
                key = groupFor.GetValueOrDefault(("Coordination", session), "coordination:" + session.ToString("N"));
            key ??= "incident:" + incident.Id.ToString("N");
            var g = Get(key, incident.WorkstreamId, incident.ReporterName, "Project recovery", "The manager is reconciling the affected work and current evidence.",
                incident.IsStatusReport ? "No response requested; the accountable manager owns recovery." : incident.RecommendedAction, incident.OwnerReport ?? incident.Facts,
                incident.ReporterOrganizationUserId ?? incident.ReporterEmployeeId);
            g.Incidents.Add(incident); g.Requires |= !incident.IsStatusReport;
            groupFor[("Incident", incident.Id)] = key;
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
            key ??= decisionId is Guid source ? groupFor.GetValueOrDefault(("Decision", source), "decision:" + source.ToString("N")) : "question:" + q.Decision.Id.ToString("N");
            var g = Get(key, null, q.AgentName, "Review a project decision", $"{q.AgentName} needs your direction on the recorded question.",
                "Choose a current option or open the supporting evidence before responding.", q.Decision.Prompt, q.AgentOrganizationUserId);
            g.Questions.Add(q); g.Requires = true;
        }
        return Consolidate(groups.Values.Select(g => g.Build()).ToArray());
    }

    public static OverviewIssueDirectory Consolidate(IReadOnlyList<OverviewIssueCard> cards)
    {
        cards = cards.GroupBy(c => c.Key).Select(group => group.First() with {
            RequiresUserAction = group.Any(c => c.RequiresUserAction), Unclassified = group.Any(c => c.Unclassified),
            Work = group.SelectMany(c => c.Work).DistinctBy(w => w.ItemId).ToArray(),
            Questions = group.SelectMany(c => c.Questions).DistinctBy(q => q.Decision.Id).ToArray(),
            Incidents = group.SelectMany(c => c.Incidents).DistinctBy(i => i.Id).ToArray(),
            Detail = string.Join("\n\n", group.Select(c => c.Detail).Distinct()),
            RelatedIssues = group.SelectMany(c => c.RelatedIssues).DistinctBy(c => c.Key).ToArray()
        }).ToArray();
        var result = cards.Where(c => c.RequiresUserAction || c.Status != "Open").ToList();
        foreach (var project in cards.Where(c => !c.RequiresUserAction && c.Status == "Open").GroupBy(c => c.WorkstreamId?.ToString("N") ?? c.Key))
        {
            var members = project.ToArray();
            if (members.Length == 1) { result.Add(members[0]); continue; }
            result.Add(new("team:" + project.Key, null, members[0].WorkstreamId, members[0].ProjectName,
                members[0].ProjectName + ": team work", $"The team is handling {members.Length} responsibilities. No response is requested.",
                "Project team", "The responsible managers will apply evidence and reconcile the remaining work.", false, members.Any(c => c.Unclassified), [], [], [], "") { RelatedIssues = members });
        }
        return new(result.OrderByDescending(g => g.RequiresUserAction).ThenBy(g => g.ProjectName).ThenBy(g => g.Key).ToArray());
    }

    private sealed class Builder(string key, Guid? id, Guid? project, string projectName, string title, string summary, string owner, string next, string detail, string status = "Open", Guid? ownerId = null)
    {
        public List<BlockedWorkAttentionResponse> Work { get; } = [];
        public List<PendingAgentQuestionResponse> Questions { get; } = [];
        public List<ProjectIncidentAttentionResponse> Incidents { get; } = [];
        public bool Requires { get; set; }
        public bool Unclassified { get; set; }
        public OverviewIssueCard Build() => new(key, id, project, projectName, title, summary, owner, next, Requires, Unclassified,
            Work, Questions, Incidents, string.Join("\n\n", new[] { detail }.Concat(Questions.Select(q => q.Decision.Prompt)).Concat(Work.Select(w => w.Reason)).Distinct()), status) { OwnerOrganizationUserId = ownerId };
    }
}
