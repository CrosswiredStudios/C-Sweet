using CSweet.Contracts.Communications;

namespace CSweet.Contracts.Core;

public sealed record OverviewIssueCard(string Key, Guid? IssueId, Guid? WorkstreamId, string ProjectName,
    string Title, string Summary, string OwnerName, string NextAction, bool RequiresUserAction, bool Unclassified,
    IReadOnlyList<BlockedWorkAttentionResponse> Work, IReadOnlyList<PendingAgentQuestionResponse> Questions,
    IReadOnlyList<ProjectIncidentAttentionResponse> Incidents, string Detail, string Status = "Open")
{
    public IReadOnlyList<OverviewIssueCard> RelatedIssues { get; init; } = [];
    public IReadOnlyList<OverviewAffectedWork> AffectedWork { get; init; } = [];
    public IReadOnlyList<OverviewIssueAction> Actions { get; init; } = [];
    public Guid? OwnerOrganizationUserId { get; init; }
    public bool IsComputeBlocker { get; init; }
}

public sealed record OverviewAffectedWork(Guid ItemId, string Title, string ActionUri);

public sealed record OverviewIssueAction(Guid Id, string Label, string ActionUri);

public sealed record OverviewIssueDirectory(IReadOnlyList<OverviewIssueCard> Items)
{
    public IReadOnlyList<OverviewIssueCard> UserActions => Items.Where(x => x.Status == "Open" && x.RequiresUserAction).ToArray();
    public IReadOnlyList<OverviewIssueCard> Team => Items.Where(x => x.Status == "Open" && !x.RequiresUserAction).ToArray();

    public static OverviewIssueDirectory Consolidate(IReadOnlyList<OverviewIssueCard> cards)
    {
        cards = cards.GroupBy(c => c.Key).Select(group => group.First() with {
            RequiresUserAction = group.Any(c => c.RequiresUserAction), Unclassified = group.Any(c => c.Unclassified),
            AffectedWork = group.SelectMany(c => c.AffectedWork).DistinctBy(w => w.ItemId).ToArray(),
            Work = group.SelectMany(c => c.Work).DistinctBy(w => w.ItemId).ToArray(),
            Questions = group.SelectMany(c => c.Questions).DistinctBy(q => q.Decision.Id).ToArray(),
            Incidents = group.SelectMany(c => c.Incidents).DistinctBy(i => i.Id).ToArray(),
            Actions = group.SelectMany(c => c.Actions).DistinctBy(a => a.Id).ToArray(),
            Detail = string.Join("\n\n", group.Select(c => c.Detail).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct()),
            RelatedIssues = group.SelectMany(c => c.RelatedIssues).DistinctBy(c => c.Key).ToArray()
        }).ToArray();
        return new(cards.OrderByDescending(g => g.RequiresUserAction).ThenBy(g => g.ProjectName).ThenBy(g => g.Key).ToArray());
    }
}
