using System.Collections.Concurrent;
using System.Text;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.Protocol;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public interface IAgentGuestSessionCoordinator
{
    Task StartAsync(
        IsolationWorkloadHandle handle,
        RuntimeWorkloadSpec workload,
        CancellationToken cancellationToken = default);

    Task StopAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default);

    AgentGuestSessionOutcome? GetOutcome(IsolationWorkloadHandle handle);

    string? GetLogs(IsolationWorkloadHandle handle, int maximumBytes);
}

public sealed record AgentGuestSessionOutcome(
    int ExitCode,
    string ReasonCode,
    string? SanitizedDetail);

public sealed class HyperVGuestSessionCoordinator(
    IHyperVGuestTransport transport,
    IAgentBrokerOperationHandler operationHandler,
    TimeProvider timeProvider,
    ILogger<HyperVGuestSessionCoordinator> logger) : IAgentGuestSessionCoordinator
{
    private readonly ConcurrentDictionary<Guid, ActiveSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, AgentGuestSessionOutcome> _outcomes = new();
    private readonly ConcurrentDictionary<Guid, RuntimeGuestLogStreamHandler> _logs = new();

    public async Task StartAsync(
        IsolationWorkloadHandle handle,
        RuntimeWorkloadSpec workload,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(handle.ProviderId, IsolationProviderCatalog.HyperV().ProviderId, StringComparison.Ordinal))
            throw new IsolationUnavailableException("The selected provider does not yet have a registered guest transport coordinator.");
        if (!Guid.TryParseExact(handle.ProviderInstanceId, "N", out var virtualMachineId) || virtualMachineId == Guid.Empty)
            throw new InvalidDataException("The Hyper-V provider did not return its VM identifier.");
        if (handle.WorkloadId != workload.WorkloadId || handle.Kind != IsolationWorkloadKind.Runtime)
            throw new InvalidDataException("The guest session workload binding is invalid.");
        var stream = await transport.ConnectAsync(virtualMachineId, cancellationToken);
        var lifetime = new CancellationTokenSource();
        var grant = new AgentBrokerGrant(
            workload.WorkloadId,
            workload.BrokerLease.ChannelId,
            workload.Identity.InstallationId,
            workload.GuestImage.Digest,
            workload.Artifact.Digest,
            workload.BrokerLease.ProtocolVersion,
            workload.BrokerLease.BootToken,
            workload.BrokerLease.ExpiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "mcp.runtime" },
            MaximumRequestCount: 100_000,
            MaximumRequestBodyBytes: 1024 * 1024,
            MaximumResponseBodyBytes: 16 * 1024 * 1024,
            MaximumFrameBytes: 16 * 1024 * 1024);
        var boot = new GuestBootConfiguration
        {
            WorkloadId = workload.WorkloadId.ToString("D"),
            ChannelId = workload.BrokerLease.ChannelId.ToString("D"),
            ProtocolVersion = workload.BrokerLease.ProtocolVersion,
            GuestImageDigest = workload.GuestImage.Digest,
            ArtifactDigest = workload.Artifact.Digest,
            BootToken = workload.BrokerLease.BootToken,
            LeaseExpiresAtUnixSeconds = workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds(),
            ArtifactRoot = "/run/csweet/artifact/payload",
            WorkloadKind = (int)IsolationWorkloadKind.Runtime,
            InstallationId = workload.Identity.InstallationId.ToString("D"),
            BusinessId = workload.Identity.BusinessId,
            TickId = workload.Identity.TickId.ToString("D"),
            LocalBrokerSocketPath = "/run/csweet/broker.sock",
            WorkloadTokenPath = "/run/csweet/workload-token",
            MaximumFrameBytes = 16 * 1024 * 1024
        };
        var start = new StartCommand
        {
            WorkloadKind = (int)IsolationWorkloadKind.Runtime,
            MaximumLogBytes = workload.ResourceLimits.MaximumLogBytes
        };
        start.Entrypoint.AddRange(workload.Entrypoint);
        var logStream = new RuntimeGuestLogStreamHandler(workload.WorkloadId, workload.Identity.InstallationId);
        if (!_logs.TryAdd(handle.WorkloadId, logStream))
            throw new InvalidOperationException("A guest log stream is already active for this workload.");
        var session = new GuestBrokerHostSession(
            grant, operationHandler, timeProvider, logStream,
            bootConfiguration: boot, startCommand: start);
        var run = RunSessionAsync(handle, session, stream, lifetime);
        var active = new ActiveSession(lifetime, run);
        if (!_sessions.TryAdd(handle.WorkloadId, active))
        {
            _logs.TryRemove(handle.WorkloadId, out _);
            lifetime.Cancel();
            await stream.DisposeAsync();
            throw new InvalidOperationException("A guest broker session is already active for this workload.");
        }
        var completed = await Task.WhenAny(session.Started, run).WaitAsync(cancellationToken);
        if (completed == run) await run;
        await session.Started.WaitAsync(cancellationToken);
        await Task.Yield();
        if (run.IsCompleted) await run;
    }

    public async Task StopAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(handle.WorkloadId, out var active))
        {
            active.Lifetime.Cancel();
            try { await active.RunTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or GuestWorkloadExitedException)
            {
                logger.LogDebug(exception, "Guest broker session {WorkloadId} stopped.", handle.WorkloadId);
            }
            active.Lifetime.Dispose();
        }
        _outcomes.TryRemove(handle.WorkloadId, out _);
        _logs.TryRemove(handle.WorkloadId, out _);
    }

    public AgentGuestSessionOutcome? GetOutcome(IsolationWorkloadHandle handle) =>
        _outcomes.TryGetValue(handle.WorkloadId, out var outcome) ? outcome : null;

    public string? GetLogs(IsolationWorkloadHandle handle, int maximumBytes) =>
        _logs.TryGetValue(handle.WorkloadId, out var logs) ? logs.Read(maximumBytes) : null;

    private async Task RunSessionAsync(
        IsolationWorkloadHandle handle,
        GuestBrokerHostSession session,
        Stream stream,
        CancellationTokenSource lifetime)
    {
        try
        {
            await using (stream)
                await session.RunAsync(stream, stream, lifetime.Token);
            if (!lifetime.IsCancellationRequested)
                _outcomes[handle.WorkloadId] = new AgentGuestSessionOutcome(
                    session.WorkloadExit?.ExitCode ?? 0,
                    string.IsNullOrWhiteSpace(session.WorkloadExit?.ReasonCode)
                        ? "process-exited"
                        : session.WorkloadExit.ReasonCode,
                    string.IsNullOrWhiteSpace(session.WorkloadExit?.Detail)
                        ? null
                        : Sanitize(session.WorkloadExit.Detail));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (GuestWorkloadExitedException exception)
        {
            _outcomes[handle.WorkloadId] = new AgentGuestSessionOutcome(
                exception.ExitCode,
                exception.ReasonCode,
                exception.SanitizedDetail);
            logger.LogWarning(exception, "Guest workload {WorkloadId} exited.", handle.WorkloadId);
            throw;
        }
        catch (Exception exception)
        {
            _outcomes[handle.WorkloadId] = new AgentGuestSessionOutcome(
                1,
                "broker-session-failed",
                Sanitize(exception.Message));
            logger.LogError(exception, "Guest broker session {WorkloadId} failed.", handle.WorkloadId);
            throw;
        }
        finally
        {
            _sessions.TryRemove(handle.WorkloadId, out _);
            lifetime.Dispose();
        }
    }

    private static string Sanitize(string value) => new(value
        .Where(character => !char.IsControl(character) || character == ' ')
        .Take(1000)
        .ToArray());

    private sealed record ActiveSession(CancellationTokenSource Lifetime, Task RunTask);

    private sealed class RuntimeGuestLogStreamHandler(Guid workloadId, Guid installationId) : IGuestBrokerStreamHandler
    {
        private readonly object _gate = new();
        private byte[] _snapshot = [];
        private long _nextSequence;

        public Task HandleAsync(GuestBrokerStreamContext chunk, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.WorkloadId != workloadId || chunk.InstallationId != installationId ||
                !string.Equals(chunk.StreamId, "runtime.logs", StringComparison.Ordinal) ||
                chunk.Sequence != _nextSequence || chunk.Completed || chunk.Digest is not null ||
                chunk.Content.Length > 16 * 1024)
                throw new InvalidDataException("The runtime guest log stream is invalid.");
            lock (_gate)
            {
                _snapshot = chunk.Content.ToArray();
                _nextSequence++;
            }
            return Task.CompletedTask;
        }

        public string Read(int maximumBytes)
        {
            lock (_gate)
            {
                var start = Math.Max(0, _snapshot.Length - maximumBytes);
                return Encoding.UTF8.GetString(_snapshot, start, _snapshot.Length - start);
            }
        }
    }
}
