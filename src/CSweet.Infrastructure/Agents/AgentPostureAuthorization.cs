using CSweet.Application.Agents;
using CSweet.Infrastructure.Persistence;

namespace CSweet.Infrastructure.Agents;

/// <summary>Binds posture changes to the same owner/manager rule the governance endpoints use.</summary>
public sealed class AgentPostureAuthorization(CSweetDbContext db) : IAgentPostureAuthorization
{
    public async ValueTask<bool> CanChangeAsync(
        Guid installationId,
        Guid applicationUserId,
        bool administrator,
        CancellationToken cancellationToken = default) =>
        await AgentGovernanceAccess.CanManageAsync(db, applicationUserId, installationId, administrator, cancellationToken);
}
