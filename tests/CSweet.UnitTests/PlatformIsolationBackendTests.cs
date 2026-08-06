using CSweet.AgentRuntime.AppleVirtualization;
using CSweet.AgentRuntime.Firecracker;
using CSweet.AgentRuntime.HyperV;

namespace CSweet.UnitTests;

public sealed class PlatformIsolationBackendTests
{
    [Fact]
    public async Task HyperV_FailsClosedWithoutInstalledHelperAndCertification()
    {
        var backend = new HyperVIsolationBackend(new HyperVIsolationBackendOptions(), TimeProvider.System);
        var result = await backend.ProbeAsync();
        Assert.False(result.IsAvailable);
        Assert.Null(result.Certification);
    }

    [Fact]
    public async Task Firecracker_FailsClosedOnNonLinuxOrMissingHelper()
    {
        var backend = new FirecrackerIsolationBackend(new FirecrackerIsolationBackendOptions(), TimeProvider.System);
        var result = await backend.ProbeAsync();
        Assert.False(result.IsAvailable);
        Assert.Null(result.Certification);
    }

    [Fact]
    public async Task AppleVirtualization_FailsClosedOnNonMacOrMissingHelper()
    {
        var backend = new AppleVirtualizationIsolationBackend(new AppleVirtualizationIsolationBackendOptions(), TimeProvider.System);
        var result = await backend.ProbeAsync();
        Assert.False(result.IsAvailable);
        Assert.Null(result.Certification);
    }
}
