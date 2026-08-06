using System.Collections.Concurrent;
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
}

public sealed class HyperVGuestSessionCoordinator(
    IHyperVGuestTransport transport,
    IAgentBrokerOperationHandler operationHandler,
    TimeProvider timeProvider,
    ILogger<HyperVGuestSessionCoordinator> logger) : IAgentGuestSessionCoordinator
{
    private readonly ConcurrentDictionary<Guid, ActiveSession> _sessions = new();

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
        var session = new GuestBrokerHostSession(
            grant, operationHandler, timeProvider,
            bootConfiguration: boot, startCommand: start);
        var run = RunSessionAsync(handle, session, stream, lifetime);
        var active = new ActiveSession(lifetime, run);
        if (!_sessions.TryAdd(handle.WorkloadId, active))
        {
            lifetime.Cancel();
            await stream.DisposeAsync();
            throw new InvalidOperationException("A guest broker session is already active for this workload.");
        }
        var completed = await Task.WhenAny(session.Started, run).WaitAsync(cancellationToken);
        if (completed == run) await run;
        await session.Started;
    }

    public async Task StopAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(handle.WorkloadId, out var active)) return;
        active.Lifetime.Cancel();
        try { await active.RunTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
        {
            logger.LogDebug(exception, "Guest broker session {WorkloadId} stopped.", handle.WorkloadId);
        }
        active.Lifetime.Dispose();
    }

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
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Guest broker session {WorkloadId} failed.", handle.WorkloadId);
            throw;
        }
        finally
        {
            _sessions.TryRemove(handle.WorkloadId, out _);
        }
    }

    private sealed record ActiveSession(CancellationTokenSource Lifetime, Task RunTask);
}
