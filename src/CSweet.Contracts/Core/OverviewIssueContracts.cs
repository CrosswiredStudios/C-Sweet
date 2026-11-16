using CSweet.Contracts.Communications;

namespace CSweet.Contracts.Core;

public sealed record OverviewIssueCard(string Key, Guid? IssueId, Guid? WorkstreamId, string ProjectName,
    string Title, string Summary, string OwnerName, string NextAction, bool RequiresUserAction, bool Unclassified,
    IReadOnlyList<BlockedWorkAttentionResponse> Work, IReadOnlyList<PendingAgentQuestionResponse> Questions,
    IReadOnlyList<ProjectIncidentAttentionResponse> Incidents, string Detail, string Status = "Open")
{
    public IReadOnlyList<OverviewIssueCard> RelatedIssues { get; init; } = [];
    public Guid? OwnerOrganizationUserId { get; init; }
}

public sealed record OverviewIssueDirectory(IReadOnlyList<OverviewIssueCard> Items)
{
    public IReadOnlyList<OverviewIssueCard> UserActions => Items.Where(x => x.Status == "Open" && x.RequiresUserAction).ToArray();
    public IReadOnlyList<OverviewIssueCard> Team => Items.Where(x => x.Status == "Open" && !x.RequiresUserAction).ToArray();
}
