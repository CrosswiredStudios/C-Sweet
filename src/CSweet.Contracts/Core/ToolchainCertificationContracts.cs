namespace CSweet.Contracts.Core;

public sealed record StartToolchainCertificationRequest(
    Guid OrganizationId,
    Guid ToolchainDefinitionId,
    Guid ProviderInstallationId,
    string EnvironmentProfileKey,
    string EnvironmentImageDigest,
    int ValidForDays = 90);

public sealed record RevokeToolchainCertificationRequest(string Reason, long ExpectedRevision);

public sealed record ToolchainCertificationSummary(
    Guid Id,
    Guid OrganizationId,
    Guid ToolchainDefinitionId,
    Guid ProviderInstallationId,
    string EnvironmentProfileKey,
    string EnvironmentImageDigest,
    string Status,
    int ScheduledBuilds,
    int CompletedBuilds,
    string ChecksJson,
    string? FirstManifestHash,
    string? SecondManifestHash,
    string? RevocationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ExpiresAt,
    long Revision);
