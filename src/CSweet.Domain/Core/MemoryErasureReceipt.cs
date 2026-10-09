namespace CSweet.Domain.Core;

/// <summary>Immutable content-free authorization, replay and reset-discovery evidence.</summary>
public sealed class MemoryErasureReceipt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid EpisodeId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorApplicationUserId { get; set; }
    public Guid ActorOrganizationUserId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string InventoryJson { get; set; } = string.Empty;
    public int ErasedRecords { get; set; }
    public int ErasedRevisions { get; set; }
    public int ClearedJobs { get; set; }
    public int ClearedWorks { get; set; }
    public int ClearedModelRuns { get; set; }
    public int ClearedDiagnosticTurns { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
