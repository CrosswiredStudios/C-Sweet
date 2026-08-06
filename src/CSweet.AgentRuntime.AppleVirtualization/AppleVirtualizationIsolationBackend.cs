using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.AppleVirtualization;

public sealed class AppleVirtualizationIsolationBackendOptions : PlatformIsolationBackendOptions;

public sealed class AppleVirtualizationIsolationBackend(AppleVirtualizationIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.AppleVirtualization(), options, timeProvider)
{
    protected override bool IsHostPlatform(out string unavailableReason)
    {
        unavailableReason = OperatingSystem.IsMacOS()
            ? string.Empty
            : "Virtualization.framework requires a macOS host.";
        return OperatingSystem.IsMacOS();
    }
}
