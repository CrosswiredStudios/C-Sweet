using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentRuntimeStartupCleanupServiceTests
{
    [Fact]
    public async Task CleanupAsync_RemovesPreviousRuntimeContainersAndNetworks()
    {
        var runtimeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var runner = new StartupCleanupRunner([
            new AgentManagedContainer("container-id", $"csweet-agent-{runtimeId:N}", runtimeId)
        ]);
        var service = CreateService(runner, new AgentRuntimeManagerOptions
        {
            DockerNetworkName = "csweet-runtime",
            McpGatewayContainer = "agenthost"
        });

        var removed = await service.CleanupAsync();

        Assert.Equal(1, removed);
        Assert.Equal(["container-id"], runner.Removed);
        Assert.Equal([($"csweet-runtime-{runtimeId:N}", "agenthost")], runner.NetworksRemoved);
    }

    [Fact]
    public async Task CleanupAsync_CanBeDisabledForMultiSchedulerDeployments()
    {
        var runner = new StartupCleanupRunner([
            new AgentManagedContainer("container-id", "csweet-agent-11111111111111111111111111111111", Guid.NewGuid())
        ]);
        var service = CreateService(runner, new AgentRuntimeManagerOptions
        {
            CleanupContainersOnStartup = false
        });

        var removed = await service.CleanupAsync();

        Assert.Equal(0, removed);
        Assert.False(runner.WasListed);
        Assert.Empty(runner.Removed);
    }

    private static AgentRuntimeStartupCleanupService CreateService(
        IAgentContainerRunner runner,
        AgentRuntimeManagerOptions options) =>
        new(runner, Options.Create(options), NullLogger<AgentRuntimeStartupCleanupService>.Instance);

    private sealed class StartupCleanupRunner(IReadOnlyList<AgentManagedContainer> managed) : IAgentContainerRunner
    {
        public bool WasListed { get; private set; }
        public List<string> Removed { get; } = [];
        public List<(string NetworkName, string McpGatewayContainer)> NetworksRemoved { get; } = [];

        public Task<IReadOnlyList<AgentManagedContainer>> ListManagedAsync(CancellationToken cancellationToken = default)
        {
            WasListed = true;
            return Task.FromResult(managed);
        }

        public Task RemoveAsync(string containerId, bool force = false, CancellationToken cancellationToken = default)
        {
            Assert.True(force);
            Removed.Add(containerId);
            return Task.CompletedTask;
        }

        public Task RemoveNetworkAsync(string networkName, string brokerGatewayContainer, CancellationToken cancellationToken = default)
        {
            NetworksRemoved.Add((networkName, brokerGatewayContainer));
            return Task.CompletedTask;
        }

        public Task<AgentContainerStatus> StartAsync(AgentContainerStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetLogsAsync(string containerId, int maximumBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
