using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentRuntimeSignalService(CSweetDbContext dbContext) : IAgentRuntimeSignalService
{
    public async Task RecordMcpSessionEstablishedAsync(Guid runtimeInstanceId, Guid tickId, Guid installationId, string workloadToken, CancellationToken cancellationToken = default)
    {
        var instance = await dbContext.AgentRuntimeInstances
            .Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Schedule)
            .SingleOrDefaultAsync(x => x.Id == runtimeInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("The runtime instance was not found.");
        ValidateIdentity(instance, tickId, installationId);
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(workloadToken));
        var storedHash = Convert.FromHexString(instance.WorkloadTokenHash);
        if (!CryptographicOperations.FixedTimeEquals(presentedHash, storedHash))
            throw new InvalidOperationException("The runtime workload token is invalid.");
        // An SDK MCP session can reconnect while the same runtime is still running.
        // Identity and workload-token validation above make this retry safe; rejecting it
        // leaves the durable runtime marked Running with no live broker session.
        if (instance.Status == AgentRuntimeStatus.Running)
            return;
        if (instance.Status != AgentRuntimeStatus.WaitingForMcpSession)
            throw new InvalidOperationException("The runtime instance is not awaiting MCP session establishment.");
        if (instance.AgentInstallation?.Schedule is { } schedule)
        {
            schedule.ConsecutiveStartupFailures = 0;
            schedule.AutomaticStartSuppressedAt = null;
        }
        AddTransition(instance, AgentRuntimeStatus.Running, DateTimeOffset.UtcNow, "MCP session established.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordCompletionAsync(Guid runtimeInstanceId, Guid tickId, Guid installationId, string payloadJson, CancellationToken cancellationToken = default)
    {
        var instance = await dbContext.AgentRuntimeInstances.SingleOrDefaultAsync(x => x.Id == runtimeInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("The runtime instance was not found.");
        ValidateIdentity(instance, tickId, installationId);
        if (instance.Status != AgentRuntimeStatus.Running)
            throw new InvalidOperationException("Only a running runtime instance may report completion.");
        AddTransition(instance, AgentRuntimeStatus.CompletionReported, DateTimeOffset.UtcNow, "Agent reported completion.", payloadJson);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateIdentity(AgentRuntimeInstance instance, Guid tickId, Guid installationId)
    {
        if (instance.TickId != tickId || instance.AgentInstallationId != installationId)
            throw new InvalidOperationException("The runtime identity does not match the installation and tick.");
    }

    private void AddTransition(AgentRuntimeInstance instance, AgentRuntimeStatus status, DateTimeOffset at, string? reason = null, string? payload = null)
    {
        instance.TransitionTo(status, at, reason);
        dbContext.AgentRuntimeEvents.Add(new AgentRuntimeEvent { Id = Guid.NewGuid(), AgentRuntimeInstanceId = instance.Id, Status = status, Reason = reason, PayloadJson = payload, OccurredAt = at });
    }
}
