namespace CSweet.Domain.Core;

/// <summary>Durable accepted non-conversation input; identity and source snapshot never change.</summary>
public sealed class MemoryEpisodeEnrichmentJob
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid? ReviewerApplicationUserId { get; set; }
    public Guid EpisodeId { get; set; }
    public int InputGeneration { get; set; }
    public Guid? PreviousJobId { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public string SourceJson { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public MemoryCaptureStatus Status { get; set; }
    public int Attempts { get; set; }
    public int RetryGeneration { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? AcceptedExtractionJson { get; set; }
    public DateTimeOffset? ExtractionAcceptedAt { get; set; }
}

/// <summary>Content-free, append-only evidence of a human-authorized episode retry.</summary>
public sealed class MemoryEpisodeRetryReceipt
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid OperationId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public int PreviousGeneration { get; set; }
    public int RetryGeneration { get; set; }
    public int PreviousAttempts { get; set; }
    public bool ReusesAcceptedExtraction { get; set; }
    public string SourceHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only proof of input observed before a generic extraction attempt.</summary>
public sealed class MemoryEpisodeExtractionReceipt
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid LeaseToken { get; set; }
    public string SourceHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
