namespace CSweet.Contracts.Planning;

public sealed record PlanningActionResponse(
    bool Succeeded,
    string? ErrorCode,
    string? Message,
    PlanningRunResponse? PlanningRun = null);
