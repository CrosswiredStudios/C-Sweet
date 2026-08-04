namespace CSweet.Application.SourceControl;

public interface ISourceControlDecisionSigner
{
    string Sign(SourceControlMergeDecision decision);
    bool Verify(SourceControlMergeDecision decision, string signature);
}

public sealed record SourceControlMergeDecision(
    Guid OrganizationId,
    Guid PublicationId,
    string CommitSha,
    Guid AuthorizedByOrganizationUserId,
    long TeamPolicyRevision,
    DateTimeOffset AuthorizedAt,
    DateTimeOffset ExpiresAt);
