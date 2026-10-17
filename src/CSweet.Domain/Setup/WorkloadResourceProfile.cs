namespace CSweet.Domain.Setup;

/// <summary>Server-selected tier for one repository, command class and dependency/toolchain fingerprint.</summary>
public sealed class WorkloadResourceProfile
{
    public string Key { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public Guid RepositoryId { get; set; }
    public string CommandClass { get; set; } = string.Empty;
    public string InputFingerprint { get; set; } = string.Empty;
    public int MemoryMb { get; set; }
    public long PeakWorkloadMemoryBytes { get; set; }
    public int SuccessfulSamples { get; set; }
    public int MemoryFailures { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}
