namespace CSweet.Contracts.SourceControl;

/// <summary>Core-to-trusted-host request. Provider credentials never cross this boundary.</summary>
public sealed record DeliveryBranchOperation(Guid OrganizationId, Guid RepositoryId,
    string Provider, long? InstallationId, string Owner, string Repository,
    string Operation, string SourceBranch, string TargetBranch, string IdempotencyKey,
    string? ExpectedSourceSha = null, string? ExpectedTargetSha = null,
    string? CandidateCommitSha = null);
public sealed record DeliveryBranchResult(string SourceCommitSha, string TargetCommitSha,
    string? CandidateCommitSha, bool Promoted = false, string? Error = null);
