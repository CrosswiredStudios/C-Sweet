namespace CSweet.Contracts.Memory;

public sealed record AgentMemorySummaryResponse(
    Guid OrganizationId,
    Guid EmployeeId,
    string EmployeeName,
    Guid InstallationId,
    string AgentDefinitionId,
    string AgentName,
    int EpisodeCount,
    int ClaimCount,
    int EntityCount,
    int RelationshipCount,
    int ProcedureCount,
    DateTimeOffset? MostRecentCapture,
    int PendingCaptureCount,
    string Health);

public sealed record AgentMemoryItemResponse(
    Guid Id,
    string Kind,
    string Scope,
    Guid? UserId,
    string? UserName,
    string Title,
    string Content,
    string Source,
    string Sensitivity,
    string State,
    double? Confidence,
    DateTimeOffset OccurredAt,
    Guid? ConversationId,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<Guid>? RelatedMemoryIds = null,
    IReadOnlyList<AgentMemoryRecallUseResponse>? RecallUses = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AudienceId { get; init; }
}

public sealed record AgentMemoryRecallUseResponse(
    Guid ConversationId,
    Guid UserId,
    string Layer,
    DateTimeOffset UsedAt);

public sealed record AgentMemoryPageResponse(
    IReadOnlyList<AgentMemoryItemResponse> Items,
    string? NextCursor,
    int TotalCount);

public sealed record AgentMemoryGraphNodeResponse(
    Guid Id,
    string Label,
    string Type,
    string Scope,
    Guid? UserId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AudienceId { get; init; }
}

public sealed record AgentMemoryGraphEdgeResponse(
    Guid Id,
    Guid FromId,
    Guid ToId,
    string Label,
    double Confidence);

public sealed record AgentMemoryGraphResponse(
    IReadOnlyList<AgentMemoryGraphNodeResponse> Nodes,
    IReadOnlyList<AgentMemoryGraphEdgeResponse> Edges,
    bool Truncated);

public sealed record AgentMemoryQuery(
    string? Kind = null,
    string? Search = null,
    Guid? UserId = null,
    string? Scope = null,
    string? Source = null,
    string? Sensitivity = null,
    string? State = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Cursor = null,
    int Limit = 50);
