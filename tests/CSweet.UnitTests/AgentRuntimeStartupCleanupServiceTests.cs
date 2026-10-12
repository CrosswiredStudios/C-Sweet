using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeStartupCleanupServiceTests
{
    [Theory]
    [InlineData(IsolationWorkloadState.Running)]
    [InlineData(IsolationWorkloadState.Stopping)]
    [InlineData(IsolationWorkloadState.Failed)]
    public async Task CleanupAsync_DoesNotReportUnconfirmedRemoval(IsolationWorkloadState state)
    {
        await using var db = CreateDb();
        db.AgentRuntimeInstances.Add(new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = Guid.NewGuid(), IsolationProviderId = "test-vm", ProviderInstanceId = "vm-1" });
        await db.SaveChangesAsync();
        var runner = new StartupCleanupRunner { State = state };
        var service = new AgentRuntimeStartupCleanupService(db, runner, Options.Create(new AgentRuntimeManagerOptions()),
            NullLogger<AgentRuntimeStartupCleanupService>.Instance);
        Assert.Equal(0, await service.CleanupAsync());
        Assert.Single(runner.Destroyed);
        Assert.Equal("vm-1", (await db.AgentRuntimeInstances.SingleAsync()).ProviderInstanceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAsync_PreservesHealthyFleetWorkButCleansExpiredOrResetRuntime(bool memoryReset)
    {
        await using var db = CreateDb();
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), TickId = Guid.NewGuid(),
            AgentInstallationId = Guid.NewGuid(), IsolationProviderId = "execution-fleet", ProviderInstanceId = "vm-1",
            QueuedAt = now, RuntimeDeadlineAt = now.AddHours(1), MemoryReadEvidenceVersion = AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, now);
        runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, now);
        runtime.TransitionTo(AgentRuntimeStatus.Running, now);
        db.Add(runtime);
        db.McpAgentSessions.Add(new() { Id = Guid.NewGuid(), RuntimeInstanceId = runtime.Id,
            AgentInstallationId = runtime.AgentInstallationId, ExpiresAt = now.AddMinutes(10) });
        var work = new AgentWorkItem { Id = Guid.NewGuid(), AgentInstallationId = runtime.AgentInstallationId, Status = AgentWorkStatus.Leased };
        db.Add(work);
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
            RuntimeInstanceId = runtime.Id, LeaseExpiresAt = now.AddMinutes(3) };
        db.Add(attempt); await db.SaveChangesAsync();
        var runner = new StartupCleanupRunner();
        var service = new AgentRuntimeStartupCleanupService(db, runner, Options.Create(new AgentRuntimeManagerOptions()),
            NullLogger<AgentRuntimeStartupCleanupService>.Instance);
        Assert.Equal(0, await service.CleanupAsync()); Assert.Empty(runner.Destroyed);
        if (memoryReset) runtime.MemoryResetRequestedAt = now;
        else attempt.LeaseExpiresAt = now.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Equal(1, await service.CleanupAsync()); Assert.Single(runner.Destroyed);
        Assert.Contains(db.AgentRuntimeEvents, x => x.Reason == AgentRuntimeStartupCleanupService.RestartCleanupReason);
    }

    [Fact]
    public async Task CleanupAsync_DestroysPersistedProviderHandles()
    {
        await using var db = CreateDb();
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = Guid.NewGuid(),
            IsolationProviderId = "test-vm", ProviderInstanceId = "vm-1", QueuedAt = DateTimeOffset.UtcNow
        };
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync();
        var runner = new StartupCleanupRunner();
        var service = new AgentRuntimeStartupCleanupService(
            db, runner, Options.Create(new AgentRuntimeManagerOptions()),
            NullLogger<AgentRuntimeStartupCleanupService>.Instance);

        Assert.Equal(1, await service.CleanupAsync());
        Assert.Equal("vm-1", Assert.Single(runner.Destroyed).ProviderInstanceId);
    }

    [Fact]
    public async Task CleanupAsync_CanBeDisabledForCoordinatedDeployments()
    {
        await using var db = CreateDb();
        var runner = new StartupCleanupRunner();
        var service = new AgentRuntimeStartupCleanupService(
            db, runner, Options.Create(new AgentRuntimeManagerOptions { CleanupWorkloadsOnStartup = false }),
            NullLogger<AgentRuntimeStartupCleanupService>.Instance);

        Assert.Equal(0, await service.CleanupAsync());
        Assert.Empty(runner.Destroyed);
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class StartupCleanupRunner : IAgentWorkloadRunner
    {
        public IsolationWorkloadState State { get; init; } = IsolationWorkloadState.Stopped;
        public List<IsolationWorkloadHandle> Destroyed { get; } = [];
        public Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpecification workload, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult<IsolationWorkloadStatus?>(new(handle, State, IsolationTerminationReason.HostShutdown, 0, null, DateTimeOffset.UtcNow, null, null));
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Destroyed.Add(handle); return Task.CompletedTask; }
        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
