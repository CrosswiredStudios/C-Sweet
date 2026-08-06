using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.AgentRuntime.HyperV;

public sealed class HyperVIsolationBackendOptions : PlatformIsolationBackendOptions;

public sealed class HyperVIsolationBackend(HyperVIsolationBackendOptions options, TimeProvider timeProvider)
    : ExternalPlatformIsolationBackend(IsolationProviderCatalog.HyperV(), options, timeProvider)
{
    protected override bool IsHostPlatform(out string unavailableReason)
    {
        unavailableReason = OperatingSystem.IsWindows()
            ? string.Empty
            : "Hyper-V requires a Windows host.";
        return OperatingSystem.IsWindows();
    }
}
