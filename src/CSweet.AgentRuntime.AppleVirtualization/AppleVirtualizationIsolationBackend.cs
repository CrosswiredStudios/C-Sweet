using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.AppleVirtualization;

public sealed class AppleVirtualizationIsolationBackendOptions : PlatformIsolationBackendOptions
{
    public AppleVirtualizationIsolationBackendOptions() =>
        RequiredGuestChannelTransport = ExternalPlatformStdioGuestChannelConnector.TransportName;
}

public sealed class AppleVirtualizationIsolationBackend(AppleVirtualizationIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.AppleVirtualization(), options, timeProvider),
      IPlatformWorkloadReaper
{
    protected override bool IsHostPlatform(out string unavailableReason)
    {
        unavailableReason = OperatingSystem.IsMacOS()
            ? string.Empty
            : "Virtualization.framework requires a macOS host.";
        return OperatingSystem.IsMacOS();
    }

    Task<int> IPlatformWorkloadReaper.ReapAbandonedWorkloadsAsync(CancellationToken cancellationToken) =>
        ReapAbandonedWorkloadsAsync(cancellationToken);
}

public sealed class AppleVirtualizationGuestChannelConnector(AppleVirtualizationIsolationBackendOptions options)
    : ExternalPlatformStdioGuestChannelConnector(IsolationProviderCatalog.AppleVirtualization().ProviderId, options);
