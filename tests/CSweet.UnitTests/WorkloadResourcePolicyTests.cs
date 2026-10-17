using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;

namespace CSweet.UnitTests;

public sealed class WorkloadResourcePolicyTests
{
    [Fact]
    public void Coding_default_is_independent_of_legacy_grant_and_package_builder()
    {
        var settings = new AgentRuntimeGlobalSettings { BuildMemoryMb = 2048 };
        Assert.Equal(4096, WorkloadResourcePolicy.Initial(settings, 1024));
        Assert.Equal(16384, WorkloadResourcePolicy.Grow(4096, 16000, 16384, 0, 2));
        Assert.Equal(8192, WorkloadResourcePolicy.Grow(4096, 16000, 16384, 0, 2, confirmedMemoryFailure: true));
        Assert.Equal(16384, WorkloadResourcePolicy.Grow(8192, 9000, 16384, 1, 2));
        Assert.Throws<InvalidOperationException>(() => WorkloadResourcePolicy.Grow(16384, 17000, 16384, 2, 2));
        Assert.Throws<InvalidOperationException>(() => WorkloadResourcePolicy.Grow(4096, 8192, 4096, 0, 2));
    }
}
