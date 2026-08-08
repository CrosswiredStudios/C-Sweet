namespace CSweet.Application.Setup;

public interface IAgentRuntimeEligibilityService
{
    Task<AgentRuntimeEligibility> EvaluateAsync(Guid installationId,
        CancellationToken cancellationToken = default);
}

public sealed record AgentRuntimeEligibility(bool IsEligible, string? Reason, bool IsSystemService = false);
