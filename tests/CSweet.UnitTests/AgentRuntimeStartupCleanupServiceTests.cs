using CSweet.Application.Setup;
using CSweet.SatelliteOffice.Contracts.Workloads;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeStartupCleanupServiceTests
{
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
        public List<IsolationWorkloadHandle> Destroyed { get; } = [];
        public Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpecification workload, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult<IsolationWorkloadStatus?>(new(handle, IsolationWorkloadState.Stopped, IsolationTerminationReason.HostShutdown, 0, null, DateTimeOffset.UtcNow, null, null));
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Destroyed.Add(handle); return Task.CompletedTask; }
        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
