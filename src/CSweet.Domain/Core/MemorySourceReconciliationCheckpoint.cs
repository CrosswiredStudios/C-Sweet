namespace CSweet.Domain.Core;

/// <summary>Content-free, resumable progress for a versioned offline source repair.</summary>
public sealed class MemorySourceReconciliationCheckpoint
{
    public string Id { get; set; } = string.Empty;
    public Guid? LastEpisodeId { get; set; }
    public long ScannedEpisodes { get; set; }
    public long SuppressedEpisodes { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
