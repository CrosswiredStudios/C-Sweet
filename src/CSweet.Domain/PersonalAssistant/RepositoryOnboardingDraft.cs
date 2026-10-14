namespace CSweet.Domain.PersonalAssistant;

public sealed class RepositoryOnboardingDraft
{
    public Guid Id { get; set; }
    public Guid ApplicationUserId { get; set; }
    public string Status { get; set; } = "Sources";
    public string ReviewJson { get; set; } = "{}";
    public string AnalysisJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public long SourceRevision { get; set; } = 1;
    public Guid? AgentWorkItemId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? BriefArtifactId { get; set; }
    public string? Error { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string GitHubStateHash { get; set; } = string.Empty;
    public DateTimeOffset? GitHubStateExpiresAt { get; set; }
    public string VerifiedInstallationsJson { get; set; } = "[]";
    public string? ActionUri { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<RepositoryOnboardingSource> Sources { get; set; } = [];
}

public sealed class RepositoryOnboardingSource
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public string Kind { get; set; } = "GitHub";
    public string Name { get; set; } = string.Empty;
    public long? InstallationId { get; set; }
    public long? ExternalRepositoryId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public bool IsPrivate { get; set; }
    public string Status { get; set; } = "Selected";
    public string? Snapshot { get; set; }
    public string? ArchiveKey { get; set; }
    public string? AnalysisInputJson { get; set; }
    public string? Error { get; set; }
    public Guid RepositoryId { get; set; }
    public bool Attached { get; set; }
    public RepositoryOnboardingDraft? Draft { get; set; }
}
