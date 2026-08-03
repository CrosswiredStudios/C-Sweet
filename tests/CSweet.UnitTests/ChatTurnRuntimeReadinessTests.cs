using CSweet.Application.Setup;
using CSweet.Api.Chat;
using CSweet.Contracts.Agents;

namespace CSweet.UnitTests;

public sealed class ChatTurnRuntimeReadinessTests
{
    [Fact]
    public async Task WaitForRuntimeReadyAsync_WaitsForStartingRuntimeToEstablishSession()
    {
        var installationId = Guid.NewGuid();
        var runtime = new TransitioningRuntime(
            Readiness(installationId, AgentRuntimeReadinessStages.StartingContainer, isReady: false, isTerminal: false),
            Readiness(installationId, AgentRuntimeReadinessStages.WaitingForMcpSession, isReady: false, isTerminal: false),
            Readiness(installationId, AgentRuntimeReadinessStages.Ready, isReady: true, isTerminal: false));

        var result = await ChatTurnWorker.WaitForRuntimeReadyAsync(
            runtime,
            installationId,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(2, runtime.StatusChecks);
    }

    [Fact]
    public async Task WaitForRuntimeReadyAsync_ReturnsTerminalStartupFailure()
    {
        var installationId = Guid.NewGuid();
        var runtime = new TransitioningRuntime(
            Readiness(installationId, AgentRuntimeReadinessStages.StartingContainer, isReady: false, isTerminal: false),
            Readiness(installationId, AgentRuntimeReadinessStages.Failed, isReady: false, isTerminal: true));

        var result = await ChatTurnWorker.WaitForRuntimeReadyAsync(
            runtime,
            installationId,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.True(result.IsTerminal);
        Assert.Equal(1, runtime.StatusChecks);
    }

    private static AgentRuntimeReadinessResponse Readiness(
        Guid installationId,
        string stage,
        bool isReady,
        bool isTerminal) =>
        new(installationId, Guid.NewGuid(), stage, stage, null, null, null, null, isReady, isTerminal);

    private sealed class TransitioningRuntime(params AgentRuntimeReadinessResponse[] states)
        : IAgentInteractiveRuntimeService
    {
        private int _index;

        public int StatusChecks { get; private set; }

        public Task<AgentRuntimeReadinessResponse> EnsureReadyAsync(
            Guid installationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(states[0]);

        public Task<AgentRuntimeReadinessResponse> GetStatusAsync(
            Guid installationId,
            CancellationToken cancellationToken = default)
        {
            StatusChecks++;
            _index = Math.Min(_index + 1, states.Length - 1);
            return Task.FromResult(states[_index]);
        }
    }
}
