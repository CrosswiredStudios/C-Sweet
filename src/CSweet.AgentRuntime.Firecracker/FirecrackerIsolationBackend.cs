using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.Firecracker;

public sealed class FirecrackerIsolationBackendOptions : PlatformIsolationBackendOptions
{
    public FirecrackerIsolationBackendOptions() =>
        RequiredGuestChannelTransport = ExternalPlatformStdioGuestChannelConnector.TransportName;
}

public sealed class FirecrackerIsolationBackend(FirecrackerIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.Firecracker(), options, timeProvider),
      IPlatformWorkloadReaper
{
    protected override bool IsHostPlatform(out string unavailableReason)
    {
        unavailableReason = OperatingSystem.IsLinux()
            ? string.Empty
            : "Firecracker/KVM requires a Linux host.";
        return OperatingSystem.IsLinux();
    }

    Task<int> IPlatformWorkloadReaper.ReapAbandonedWorkloadsAsync(CancellationToken cancellationToken) =>
        ReapAbandonedWorkloadsAsync(cancellationToken);
}

public sealed class FirecrackerGuestChannelConnector(FirecrackerIsolationBackendOptions options)
    : ExternalPlatformStdioGuestChannelConnector(IsolationProviderCatalog.Firecracker().ProviderId, options);
