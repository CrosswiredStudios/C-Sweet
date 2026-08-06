using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.UnitTests;

public sealed class InMemoryIsolationProviderTests
{
    [Fact]
    public async Task Lifecycle_IsDeterministicAndDestroyIsFinal()
    {
        var provider = Provider();
        var workload = Runtime();

        var handle = await provider.CreateAsync(workload);
        Assert.Equal(IsolationWorkloadState.Created, (await provider.InspectAsync(handle))!.State);
        await provider.StartAsync(handle);
        Assert.Equal(IsolationWorkloadState.Running, (await provider.InspectAsync(handle))!.State);
        await provider.StopAsync(handle, TimeSpan.Zero);
        Assert.Equal(IsolationTerminationReason.Completed, (await provider.InspectAsync(handle))!.TerminationReason);
        await provider.DestroyAsync(handle);
        Assert.Null(await provider.InspectAsync(handle));
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateWorkloadIdentity()
    {
        var provider = Provider();
        var workload = Runtime();
        await provider.CreateAsync(workload);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(workload));
    }

    private static InMemoryAgentIsolationProvider Provider() => new(new IsolationProviderDescriptor(
        "memory-test",
        "Memory test provider",
        "1.0",
        "test",
        "test",
        0,
        new IsolationProviderCapabilities(
            IsolationAssurance.None,
            false,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            false,
            false)));

    private static RuntimeWorkloadSpec Runtime()
    {
        var digest = "sha256:" + new string('a', 64);
        var artifact = "sha256:" + new string('b', 64);
        return new RuntimeWorkloadSpec(
            Guid.NewGuid(),
            new GuestImageReference("runtime", "1.0", digest, "linux", "x64"),
            new IsolationResourceLimits(1, 100, 512, 512, 100, 1024, TimeSpan.FromMinutes(1)),
            new BrokerChannelLease(Guid.NewGuid(), "1.0", "boot", digest, artifact, DateTimeOffset.UtcNow.AddMinutes(1)),
            new AgentArtifactReference(artifact, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            ["/app/agent"]);
    }
}
