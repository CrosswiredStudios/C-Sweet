using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.Firecracker;

public sealed class FirecrackerIsolationBackendOptions : PlatformIsolationBackendOptions;

public sealed class FirecrackerIsolationBackend(FirecrackerIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.Firecracker(), options, timeProvider)
{
    protected override bool IsHostPlatform(out string unavailableReason)
    {
        unavailableReason = OperatingSystem.IsLinux()
            ? string.Empty
            : "Firecracker/KVM requires a Linux host.";
        return OperatingSystem.IsLinux();
    }
}
