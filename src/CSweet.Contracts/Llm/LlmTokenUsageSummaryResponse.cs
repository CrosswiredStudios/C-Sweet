namespace CSweet.Contracts.Llm;

public sealed record LlmTokenUsageSummaryResponse(
    DateTimeOffset GeneratedAt,
    LlmTokenUsageWindowResponse Last24Hours,
    LlmTokenUsageWindowResponse Last7Days,
    LlmTokenUsageWindowResponse Last30Days,
    IReadOnlyList<LlmProviderTokenUsageResponse> Providers,
    IReadOnlyList<AgentTokenUsageResponse> Agents)
{
    public IReadOnlyList<LlmChatTurnUsageResponse> RecentChatTurns { get; init; } = [];
}

public sealed record LlmTokenUsageWindowResponse(
    string Label,
    int RequestCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens)
{
    public int UsageReportedCallCount { get; init; }
    public long CachedInputTokens { get; init; }
    public long ReasoningTokens { get; init; }
}

public sealed record LlmChatTurnUsageResponse(
    Guid ChatTurnId,
    Guid? ConversationId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUsedAt,
    int ModelCallCount,
    int UsageReportedCallCount,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    long MessageCharacters,
    long InstructionCharacters,
    long ToolCharacters,
    long MemoryCharacters,
    IReadOnlyDictionary<string, int> CallsByPurpose);

public sealed record LlmProviderTokenUsageResponse(
    Guid ProviderProfileId,
    string ProviderName,
    LlmTokenUsageWindowResponse Usage);

public sealed record AgentTokenUsageResponse(
    string AgentKey,
    LlmTokenUsageWindowResponse Usage);
