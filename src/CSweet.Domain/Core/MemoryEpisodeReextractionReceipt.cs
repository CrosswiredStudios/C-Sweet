namespace CSweet.Domain.Core;

/// <summary>Content-free immutable evidence of an explicitly reviewed replacement extraction.</summary>
public sealed class MemoryEpisodeReextractionReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid PreviousJobId { get; set; }
    public Guid JobId { get; set; }
    public int InputGeneration { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public string RequestHash { get; set; } = "";
    public string PreviousJobHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string? PreviousAcceptedHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
