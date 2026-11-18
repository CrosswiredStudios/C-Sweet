using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

internal static class PlanningContributionDecisions
{
    internal static bool HasCurrentDirection(CSweet.Agent.SDK.ContributionAdmission basis, WorkstreamDecisionRecord? prior) =>
        prior is { Status: CSweet.WorkManagement.Contracts.DecisionStatuses.Decided, SupersededByDecisionId: null } &&
        prior.Revision == basis.PriorRevision && prior.DecidedByOrganizationUserId == basis.PriorAuthorityId;

    internal static IQueryable<WorkstreamDecisionRecord> ForBoard(CSweetDbContext db, Guid organizationId, Guid project, Guid boardId)
    {
        var query = db.WorkstreamDecisions.AsNoTracking().Where(d => d.WorkstreamId == project &&
            d.OrganizationId == organizationId && d.SupersededByDecisionId == null && d.TypeDataJson != null);
        var match = JsonSerializer.Serialize(new { kind = CSweet.Agent.SDK.ContributionAdmission.Contract, boardId });
        return db.Database.IsNpgsql()
            ? query.Where(d => EF.Functions.JsonContains(d.TypeDataJson!, match))
            : query.Where(d => d.TypeDataJson!.Contains(CSweet.Agent.SDK.ContributionAdmission.Contract));
    }
}

public sealed partial class WorkBoardService
{
    private async Task<IReadOnlyList<WorkBoardPlanningContribution>> PlanningContributionsAsync(WorkBoard board,
        IReadOnlyList<WorkTask> items, CancellationToken ct)
    {
        if (board.Kind != WorkBoardKind.Standard || board.WorkstreamId is not Guid project) return [];
        var published = items.Where(i => i.ProposalItemKey != null).Select(i => i.ProposalItemKey!).ToHashSet();
        var drafts = new Dictionary<string, WorkBoardPlanningContribution>();
        var directions = await PlanningContributionDecisions.ForBoard(db, board.OrganizationId, project, board.Id)
            .OrderByDescending(d => d.UpdatedAt).Take(64).ToListAsync(ct);
        var admissions = directions.Select(d => (Decision: d,
            Basis: CSweet.Agent.SDK.ContributionAdmission.Read(JsonSerializer.Deserialize<JsonElement>(d.TypeDataJson!))))
            .Where(a => a.Basis?.BoardId == board.Id).ToList();
        var priorIds = admissions.Select(a => a.Basis!.PriorDecisionId).Distinct().ToArray();
        var priorDirections = await db.WorkstreamDecisions.AsNoTracking().Where(d => d.OrganizationId == board.OrganizationId &&
            d.WorkstreamId == project && priorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        foreach (var (decision, candidate) in admissions)
        {
            var basis = candidate!;
            if (published.Contains(basis.StoryKey)) continue;
            var current = PlanningContributionDecisions.HasCurrentDirection(basis, priorDirections.GetValueOrDefault(basis.PriorDecisionId));
            var admitted = current && basis.Allows(decision.Status, decision.SelectedOptionId, decision.DecidedByOrganizationUserId, decision.Rationale);
            drafts.TryAdd(basis.StoryKey, new(basis.StoryKey, basis.StoryKey, !current ? "Needs current direction" : admitted ? "Ready to publish" :
                decision.Status == "Pending" ? "Awaiting decision" : "On hold",
                !current || admitted ? decision.RequestedByOrganizationUserId : basis.PriorAuthorityId, "", decision.UpdatedAt));
        }
        // Only the explicit board summary is shared. Session transcripts remain participant-scoped.
        var requests = await (from s in db.AgentCoordinationSessions.AsNoTracking()
            from t in s.Turns
            where s.OrganizationId == board.OrganizationId && s.SourceKind == "Board" && s.SourceBoardId == board.Id &&
                s.WorkstreamId == project && s.InitiatorOrganizationUserId == board.ManagerOrganizationUserId &&
                t.Ordinal == 0 && t.SpeakerOrganizationUserId == s.InitiatorOrganizationUserId && t.ArtifactPayloadJson != null &&
                t.ArtifactPayloadJson.Contains("\"boardIncrement\"")
            orderby s.UpdatedAt descending
            select new { t.ArtifactPayloadJson, s.Status, s.InitiatorOrganizationUserId, s.TargetOrganizationUserId, s.UpdatedAt })
            .Take(64).ToListAsync(ct);
        foreach (var r in requests)
        {
            using var doc = JsonDocument.Parse(r.ArtifactPayloadJson!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("boardIncrement", out var increment) ||
                increment.ValueKind != JsonValueKind.Object || !increment.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                !increment.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
                key.GetString() is not { Length: > 0 and <= 256 } story || published.Contains(story)) continue;
            var status = r.Status.ToString();
            if (status == "Cancelled") continue;
            var responding = status is "Active" or "Summarizing";
            drafts.TryAdd(story, new(story, title.GetString()!, responding ? "In refinement" :
                status == "Completed" ? "Returned for review" : "Refinement needs attention",
                responding ? r.TargetOrganizationUserId : r.InitiatorOrganizationUserId, "", r.UpdatedAt));
        }
        if (drafts.Count == 0) return [];
        var owners = drafts.Values.Select(d => d.ResponsibleEmployeeId).Distinct().ToArray();
        var names = await db.CoreOrganizationUsers.AsNoTracking().Where(u => u.OrganizationId == board.OrganizationId &&
            owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return drafts.Values.OrderByDescending(d => d.UpdatedAt).Select(d =>
            d with { ResponsibleEmployee = names.GetValueOrDefault(d.ResponsibleEmployeeId) ?? "Unassigned" }).ToArray();
    }
}
