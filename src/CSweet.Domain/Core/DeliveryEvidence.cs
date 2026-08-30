namespace CSweet.Domain.Core;

public sealed class ToolchainAdapterDefinitionRecord
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public int Version { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ProviderPackageId { get; set; } = string.Empty;
    public string ProviderPackageVersion { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";
    public string DefinitionDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ToolchainCertificationRunRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ToolchainDefinitionId { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public string EnvironmentProfileKey { get; set; } = string.Empty;
    public string EnvironmentImageDigest { get; set; } = string.Empty;
    public string ProviderPackageDigest { get; set; } = string.Empty;
    public string DefinitionDigest { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string ChecksJson { get; set; } = "[]";
    public string? FirstManifestHash { get; set; }
    public string? SecondManifestHash { get; set; }
    public string? RevocationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class ToolchainInstallationEligibilityRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ToolchainDefinitionId { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public Guid CertificationRunId { get; set; }
    public string EnvironmentProfileKey { get; set; } = string.Empty;
    public string EnvironmentImageDigest { get; set; } = string.Empty;
    public DateTimeOffset CertifiedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
}

public sealed class DeliveryBuildRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid ToolchainDefinitionId { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid? CertificationRunId { get; set; }
    public int? CertificationPass { get; set; }
    public string? CertificationFixtureKey { get; set; }
    public string? CertificationFixtureResource { get; set; }
    public string SourceRevision { get; set; } = string.Empty;
    public string RecipeKey { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;
    public string ConfigurationJson { get; set; } = "{}";
    public string DefinitionDigest { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public int Attempt { get; set; }
    public int MaximumAttempts { get; set; } = 3;
    public Guid? ClaimId { get; set; }
    public Guid? ExecutionNodeId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public string OutputsJson { get; set; } = "[]";
    public string ProvenanceJson { get; set; } = "{}";
    public string? FailureCode { get; set; }
    public string? FailureSummary { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public string? CancellationReason { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid RequestedByOrganizationUserId { get; set; }
    public Guid? RequestedByInstallationId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeliveryValidationRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid BuildId { get; set; }
    public string TypeKey { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string Summary { get; set; } = string.Empty;
    public string FindingsJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class PreviewSessionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid BuildId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string? AccessReference { get; set; }
    public string EvidenceJson { get; set; } = "[]";
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid CreatedByOrganizationUserId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EvaluationSessionRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid? BuildId { get; set; }
    public string TypeKey { get; set; } = string.Empty;
    public string PlanJson { get; set; } = "{}";
    public string ConsentPolicyKey { get; set; } = string.Empty;
    public string Status { get; set; } = "Planned";
    public string ReportJson { get; set; } = "{}";
    public string EvidenceJson { get; set; } = "[]";
    public string IdempotencyKey { get; set; } = string.Empty;
    public Guid CreatedByOrganizationUserId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ReleaseReadinessRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public string TypeKey { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string EvidenceJson { get; set; } = "[]";
    public string FindingsJson { get; set; } = "[]";
    public string IdempotencyKey { get; set; } = string.Empty;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MediaAssetReferenceGrantRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public Guid WorkstreamId { get; set; }
    public Guid AssetId { get; set; }
    public string PurposeTypeKey { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
