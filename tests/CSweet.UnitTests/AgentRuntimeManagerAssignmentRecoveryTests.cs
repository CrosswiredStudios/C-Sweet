using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class AgentRuntimeManagerTests
{
    [Fact]
    public async Task RetiredRuntimeStartsFreshIdentityForExpiredDurableWork()
    {
        await using var db = CreateDb();
        var installation = await SeedAsync(db, due: false);
        installation.Schedule!.ActivationMode = ActivationMode.OnDemand;
        installation.Schedule.NextTickAt = null;
        var old = RunningInstance(installation.Id);
        old.BrokerTokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("old-used-boot-token")));
        old.ProviderInstanceId = "lost-vm";
        var session = SessionFor(old, installation, DateTimeOffset.UtcNow.AddMinutes(10));
        var work = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            OrganizationId = installation.BusinessId, Status = AgentWorkStatus.Leased,
            DeadlineAt = DateTimeOffset.UtcNow.AddHours(1), CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 1, MaximumAttempts = 3 };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
            RuntimeInstanceId = old.Id, Attempt = 1, ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1), LeaseTokenHash = new string('a', 64) };
        db.AddRange(old, session, work, attempt);
        await db.SaveChangesAsync();
        var provider = new FakeRunner
        {
            ConfirmStop = false,
            InspectStatus = new(new("test-vm", old.Id, "lost-vm", CSweet.Office.Contracts.Workloads.WorkloadKind.Runtime),
                IsolationWorkloadState.Stopped, IsolationTerminationReason.ProviderFailure, null, null, null, null, null)
        };
        var manager = CreateManager(db, provider);
        Assert.Equal(0, await manager.EnsurePendingOnDemandRuntimesAsync());

        await manager.ReconcileAsync();

        Assert.Equal(AgentRuntimeStatus.Failed, old.Status);
        Assert.NotNull(session.RevokedAt);
        Assert.Equal(1, await manager.EnsurePendingOnDemandRuntimesAsync());
        Assert.Equal(0, await manager.EnsurePendingOnDemandRuntimesAsync());
        await manager.ReconcileAsync();
        var start = Assert.Single(provider.Starts);
        Assert.NotEqual(old.Id, start.RuntimeInstanceId);
        Assert.NotEqual(old.TickId, start.TickId);
        Assert.NotEqual("old-used-boot-token", start.WorkloadToken);
        var replacement = await db.AgentRuntimeInstances.SingleAsync(x => x.Id == start.RuntimeInstanceId);
        Assert.Equal(AgentRuntimeStatus.WaitingForMcpSession, replacement.Status);
        await new AgentRuntimeSignalService(db).RecordMcpSessionEstablishedAsync(start.RuntimeInstanceId,
            start.TickId, start.InstallationId, start.WorkloadToken);
        Assert.Equal(AgentRuntimeStatus.Running, replacement.Status);
        Assert.Equal(AgentWorkStatus.Leased, work.Status); // Only the inbox may recover/claim the saved work.
    }
}
