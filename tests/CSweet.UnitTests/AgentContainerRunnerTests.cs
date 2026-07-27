using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentContainerRunnerTests
{
    [Fact]
    public async Task StartAsync_AppliesLimitsAndOnlyApprovedEnvironment()
    {
        var docker = new FakeDockerCommandExecutor(
            new DockerCommandResult(0, "[]", string.Empty),
            new DockerCommandResult(0, string.Empty, string.Empty),
            new DockerCommandResult(0, "container-id\n", string.Empty),
            new DockerCommandResult(0, InspectJson, string.Empty));
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);

        var status = await runner.StartAsync(CreateRequest());

        Assert.Equal(AgentContainerState.Running, status.State);
        Assert.Equal(["network", "inspect", "csweet-mcp"], docker.Commands[0]);
        Assert.Equal(["network", "connect", "--alias", "agenthost", "csweet-mcp", "agenthost"], docker.Commands[1]);
        var args = docker.Commands[2];
        Assert.Contains("--read-only", args);
        Assert.Contains("ALL", args);
        Assert.Contains("no-new-privileges=true", args);
        Assert.Contains("512m", args);
        Assert.Contains("0.5", args);
        Assert.Contains("100", args);
        Assert.Contains("type=bind,source=C:\\packages\\agent,target=/app,readonly", args);
        Assert.DoesNotContain("--privileged", args);
        Assert.DoesNotContain(args, value => value.Contains("docker.sock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(args, value => value.Contains("ConnectionStrings", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(14, args.Count(value => value == "--env"));
        Assert.Contains("CSweet__Agent__InstallationId=22222222-2222-2222-2222-222222222222", args);
        Assert.Contains("CSweet__Agent__McpEndpoint=http://agenthost:8081/mcp", args);
        Assert.Contains("CSweet__Agent__WorkloadTokenFile=/run/secrets/csweet-workload-token", args);
        Assert.DoesNotContain(args, value => value.StartsWith("CSWEET_MCP_TOKEN=", StringComparison.Ordinal));
        Assert.Contains("com.csweet.agent-runtime=true", args);
        Assert.Contains("com.csweet.runtime-instance-id=11111111111111111111111111111111", args);
        Assert.Contains("/bin/bash", args);
        var watchdogScript = Assert.Single(args, value => value.Contains("C-Sweet MCP session watchdog", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', watchdogScript);
    }

    [Fact]
    public async Task StartAsync_CreatesMissingRuntimeNetworkBeforeContainer()
    {
        var docker = new FakeDockerCommandExecutor(
            new DockerCommandResult(1, string.Empty, "network csweet-mcp not found"),
            new DockerCommandResult(0, "network-id", string.Empty),
            new DockerCommandResult(0, string.Empty, string.Empty),
            new DockerCommandResult(0, "container-id\n", string.Empty),
            new DockerCommandResult(0, InspectJson, string.Empty));
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);

        await runner.StartAsync(CreateRequest());

        Assert.Equal(["network", "create", "--driver", "bridge", "--internal", "csweet-mcp"], docker.Commands[1]);
        Assert.Equal(["network", "connect", "--alias", "agenthost", "csweet-mcp", "agenthost"], docker.Commands[2]);
        Assert.Equal("run", docker.Commands[3][0]);
    }

    [Fact]
    public async Task RemoveNetworkAsync_DetachesMcpGatewayAndRemovesRuntimeNetwork()
    {
        var docker = new FakeDockerCommandExecutor(
            new DockerCommandResult(0, "[]", string.Empty),
            new DockerCommandResult(0, string.Empty, string.Empty),
            new DockerCommandResult(0, "csweet-mcp", string.Empty));
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);

        await runner.RemoveNetworkAsync("csweet-mcp", "agenthost");

        Assert.Equal(["network", "inspect", "csweet-mcp"], docker.Commands[0]);
        Assert.Equal(["network", "disconnect", "--force", "csweet-mcp", "agenthost"], docker.Commands[1]);
        Assert.Equal(["network", "rm", "csweet-mcp"], docker.Commands[2]);
    }

    [Fact]
    public async Task RemoveNetworkAsync_MissingNetworkIsAlreadyClean()
    {
        var docker = new FakeDockerCommandExecutor(
            new DockerCommandResult(1, string.Empty, "Error: No such network: csweet-mcp"));
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);

        await runner.RemoveNetworkAsync("csweet-mcp", "agenthost");

        Assert.Single(docker.Commands);
    }

    [Fact]
    public async Task ListManagedAsync_ReturnsOnlyReservedRuntimeNames()
    {
        var docker = new FakeDockerCommandExecutor(new DockerCommandResult(0, """
            runtime-id	csweet-agent-11111111111111111111111111111111
            build-id	csweet-agent-build-22222222222222222222222222222222
            friendly-id	csweet-agent-not-managed
            """, string.Empty));
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);

        var managed = await runner.ListManagedAsync();

        var container = Assert.Single(managed);
        Assert.Equal("runtime-id", container.ContainerId);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), container.RuntimeInstanceId);
        Assert.Equal(
            ["ps", "--all", "--filter", "name=csweet-agent-", "--format", "{{.ID}}\t{{.Names}}"],
            docker.Commands[0]);
    }

    [Fact]
    public void RuntimeOptions_DefaultToPrivateMcpGateway()
    {
        var options = new AgentRuntimeManagerOptions();

        Assert.Equal("http://agenthost:8081/mcp", options.McpEndpoint);
        Assert.Equal("agenthost", options.McpGatewayContainer);
    }

    [Fact]
    public async Task StartAsync_UsesEndpointHostAsAliasForConfiguredGatewayContainer()
    {
        var docker = new FakeDockerCommandExecutor(
            new DockerCommandResult(0, "[]", string.Empty),
            new DockerCommandResult(0, string.Empty, string.Empty),
            new DockerCommandResult(0, "container-id\n", string.Empty),
            new DockerCommandResult(0, InspectJson, string.Empty));
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);
        var request = CreateRequest() with
        {
            McpEndpoint = "http://csweet-agenthost:8081/mcp",
            McpGatewayContainer = "agenthost"
        };

        await runner.StartAsync(request);

        Assert.Equal(
            ["network", "connect", "--alias", "csweet-agenthost", "csweet-mcp", "agenthost"],
            docker.Commands[1]);
    }

    [Fact]
    public async Task StartAsync_RejectsUnsafeEntryAssemblyBeforeDockerRuns()
    {
        var docker = new FakeDockerCommandExecutor();
        var runner = new DockerAgentContainerRunner(docker, NullLogger<DockerAgentContainerRunner>.Instance);
        var request = CreateRequest() with { EntryAssembly = "../escape.dll" };

        await Assert.ThrowsAsync<AgentContainerException>(() => runner.StartAsync(request));

        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task StartAsync_RejectsInvalidWatchdogTimingBeforeDockerRuns()
    {
        var docker = new FakeDockerCommandExecutor();
        var runner = new DockerAgentContainerRunner(
            docker,
            Options.Create(new AgentRuntimeManagerOptions
            {
                SessionWatchdogIntervalSeconds = 10,
                SessionDisconnectShutdownSeconds = 5
            }),
            NullLogger<DockerAgentContainerRunner>.Instance);

        await Assert.ThrowsAsync<AgentContainerException>(() => runner.StartAsync(CreateRequest()));

        Assert.Empty(docker.Commands);
    }

    [Fact]
    public async Task StartAsync_CanDisableSessionWatchdog()
    {
        var docker = new FakeDockerCommandExecutor(
            new DockerCommandResult(0, "[]", string.Empty),
            new DockerCommandResult(0, string.Empty, string.Empty),
            new DockerCommandResult(0, "container-id\n", string.Empty),
            new DockerCommandResult(0, InspectJson, string.Empty));
        var runner = new DockerAgentContainerRunner(
            docker,
            Options.Create(new AgentRuntimeManagerOptions { SessionWatchdogEnabled = false }),
            NullLogger<DockerAgentContainerRunner>.Instance);

        await runner.StartAsync(CreateRequest());

        var args = docker.Commands[2];
        Assert.Equal(9, args.Count(value => value == "--env"));
        Assert.DoesNotContain("/bin/bash", args);
        Assert.Equal(["dotnet", "/app/Example.Agent.dll"], args.TakeLast(2));
    }

    [Fact]
    public async Task FakeRunner_CanDriveRuntimeManagerTestsWithoutDocker()
    {
        IAgentContainerRunner runner = new FakeAgentContainerRunner();

        var status = await runner.StartAsync(CreateRequest());
        await runner.StopAsync(status.ContainerId, TimeSpan.FromSeconds(5));

        Assert.Equal(AgentContainerState.Running, status.State);
    }

    private static AgentContainerStartRequest CreateRequest() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "com.example.agent", "business-1",
        "csweet-agent-test", "mcr.microsoft.com/dotnet/runtime:9.0",
        "C:\\packages\\agent", "Example.Agent.dll", "http://agenthost:8081/mcp", "bounded-token",
        "/app/csweet-plugin.json", "csweet-mcp", 512, 50, 100, 600);

    private const string InspectJson = """
        {"Id":"container-id","Name":"/csweet-agent-test","State":{"Status":"running","ExitCode":0,"StartedAt":"2026-07-14T01:02:03Z","FinishedAt":"0001-01-01T00:00:00Z","Error":""}}
        """;

    private sealed class FakeDockerCommandExecutor(params DockerCommandResult[] results) : IDockerCommandExecutor
    {
        private readonly Queue<DockerCommandResult> _results = new(results);
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<DockerCommandResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeAgentContainerRunner : IAgentContainerRunner
    {
        public Task<AgentContainerStatus> StartAsync(AgentContainerStartRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentContainerStatus("fake", request.ContainerName, AgentContainerState.Running, null, DateTimeOffset.UtcNow, null, null));
        public Task StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken = default) => Task.FromResult<AgentContainerStatus?>(null);
        public Task<IReadOnlyList<AgentManagedContainer>> ListManagedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentManagedContainer>>([]);
        public Task RemoveAsync(string containerId, bool force = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveNetworkAsync(string networkName, string brokerGatewayContainer, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetLogsAsync(string containerId, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }
}
