namespace CSweet.Contracts.Core;

public sealed record BusinessSuggestionResponse(Guid Id, string Kind, string Status, long Revision,
    string Title, string Description, string ActionUri)
{
    public string Key { get; init; } = "";
    public Guid? DismissedByOrganizationUserId { get; init; }
    public DateTimeOffset? DismissedAt { get; init; }
}
public sealed record DismissBusinessSuggestionRequest(long ExpectedRevision);

public sealed record ImportedProjectAttentionResponse(Guid ProjectId, string Name, string ProfileStatus, string ProjectUri, string ReviewUri);
