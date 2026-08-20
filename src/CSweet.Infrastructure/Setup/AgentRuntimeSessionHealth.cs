using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

internal static class AgentRuntimeSessionHealth
{
    // Session creation is persisted immediately after the runtime transitions to Running.
    // Allow that transaction a short window before treating a missing session as stale.
    private static readonly TimeSpan RegistrationGrace = TimeSpan.FromSeconds(30);

    public static Task<bool> HasLiveSessionAsync(
        CSweetDbContext dbContext,
        AgentRuntimeInstance runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (runtime.McpSessionEstablishedAt is { } establishedAt &&
            establishedAt.Add(RegistrationGrace) > now)
        {
            return Task.FromResult(true);
        }

        return dbContext.McpAgentSessions
            .AsNoTracking()
            .AnyAsync(
                session => session.RuntimeInstanceId == runtime.Id &&
                    session.RevokedAt == null &&
                    session.ExpiresAt > now,
                cancellationToken);
    }
}
