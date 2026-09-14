namespace CSweet.Contracts.WorkManagement;

public sealed record PersonalTodoActivityResponse(
    string TaskStatus, string State, string Message, DateTimeOffset CheckedAt,
    string? Phase, DateTimeOffset? LastProgressAt, DateTimeOffset? LeaseExpiresAt,
    int Attempt, int MaximumAttempts, DateTimeOffset? NextReviewAt,
    string? AgentInferenceStatus, DateTimeOffset? AgentInferenceStartedAt,
    DateTimeOffset? AgentInferenceCompletedAt);
