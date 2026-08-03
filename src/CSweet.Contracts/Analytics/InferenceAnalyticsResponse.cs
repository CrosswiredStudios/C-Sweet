namespace CSweet.Contracts.Analytics;

public enum InferenceAnalyticsWindow
{
    Last24Hours,
    Last7Days,
    Last30Days
}

public sealed record InferenceAnalyticsResponse(
    string Window,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    DateTimeOffset GeneratedAt,
    InferenceAnalyticsTotalsResponse Totals,
    IReadOnlyList<EmployeeModelInferenceUsageResponse> Employees);

public sealed record InferenceAnalyticsTotalsResponse(
    int RequestCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

public sealed record EmployeeModelInferenceUsageResponse(
    Guid? EmployeeId,
    string EmployeeName,
    bool IsActive,
    string AgentKey,
    Guid? ProviderProfileId,
    string? ProviderName,
    string? Model,
    bool IsCurrentModel,
    int RequestCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    DateTimeOffset? LastUsedAt);
