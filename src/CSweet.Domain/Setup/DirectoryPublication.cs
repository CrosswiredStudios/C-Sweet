namespace CSweet.Domain.Setup;

/// <summary>
/// Outbox entry for the C-Sweet directory (IL-12): whether one C-Sweet account may use this Headquarters.
/// Headquarters raises <see cref="Revision"/> whenever the desired entry changes; a publisher sends it until
/// C-Sweet confirms it (<see cref="PublishedRevision"/>). Private installations never have rows.
/// </summary>
public sealed class DirectoryPublication
{
    public Guid Id { get; set; }
    /// <summary>The account's issuer subject (stable C-Sweet account id), never its email.</summary>
    public string Subject { get; set; } = string.Empty;
    public Guid ApplicationUserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public DirectoryPublicationState State { get; set; }
    public long Revision { get; set; } = 1;
    public long PublishedRevision { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum DirectoryPublicationState
{
    Active = 0,
    Removed = 1
}
