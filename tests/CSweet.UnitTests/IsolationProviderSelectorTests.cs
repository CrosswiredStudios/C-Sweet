using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.UnitTests;

public sealed class IsolationProviderSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SelectAsync_RejectsSharedKernelProviderForEveryTrustLevel()
    {
        var provider = new FakeProvider("docker", IsolationAssurance.SharedKernelContainer, certified: true);
        var selector = CreateSelector(provider);

        var exception = await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            selector.SelectAsync(CreateRequest(AgentTrustLevel.BuiltIn)));

        Assert.Contains("required isolation capabilities", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectAsync_RejectsAvailableProviderWithoutCertification()
    {
        var provider = new FakeProvider("hyperv", IsolationAssurance.CertifiedHardwareVirtualMachine, certified: false);
        var selector = CreateSelector(provider);

        var exception = await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            selector.SelectAsync(CreateRequest(AgentTrustLevel.UntrustedRepository)));

        Assert.Contains("no active matching certification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectAsync_RejectsCertificationForDifferentGuestImage()
    {
        var provider = new FakeProvider(
            "hyperv",
            IsolationAssurance.CertifiedHardwareVirtualMachine,
            certified: true,
            certifiedImageDigest: "sha256:different");
        var selector = CreateSelector(provider);

        await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            selector.SelectAsync(CreateRequest(AgentTrustLevel.UntrustedRepository)));
    }

    [Fact]
    public async Task SelectAsync_UsesHighestAssuranceCertifiedProvider()
    {
        var local = new FakeProvider("hyperv", IsolationAssurance.CertifiedHardwareVirtualMachine, certified: true);
        var remote = new FakeProvider("remote", IsolationAssurance.RemoteCertifiedHardwareVirtualMachine, certified: true);
        var selector = CreateSelector(local, remote);

        var selection = await selector.SelectAsync(CreateRequest(AgentTrustLevel.UntrustedMarketplace));

        Assert.Equal("remote", selection.Provider.Descriptor.ProviderId);
    }

    [Fact]
    public async Task SelectAsync_DiscoversActiveCertifiedImageWhenDigestIsNotPinned()
    {
        var provider = new FakeProvider(
            "hyperv",
            IsolationAssurance.CertifiedHardwareVirtualMachine,
            certified: true,
            certifiedImageDigest: "sha256:certified");
        var selector = CreateSelector(provider);
        var request = CreateRequest(AgentTrustLevel.UntrustedRepository) with { GuestImageDigest = null };

        var selection = await selector.SelectAsync(request);

        Assert.Equal("sha256:certified", selection.Probe.Certification!.GuestImageDigest);
    }

    [Fact]
    public async Task SelectAsync_DoesNotFallbackWhenPreferredProviderIsUnavailable()
    {
        var unavailable = new FakeProvider("hyperv", IsolationAssurance.CertifiedHardwareVirtualMachine, certified: true, available: false);
        var alternative = new FakeProvider("remote", IsolationAssurance.RemoteCertifiedHardwareVirtualMachine, certified: true);
        var selector = CreateSelector(unavailable, alternative);
        var request = CreateRequest(AgentTrustLevel.UntrustedRepository) with { PreferredProviderId = "hyperv" };

        var exception = await Assert.ThrowsAsync<IsolationUnavailableException>(() => selector.SelectAsync(request));

        Assert.Contains("hyperv", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, alternative.ProbeCount);
    }

    private static FailClosedIsolationProviderSelector CreateSelector(params IAgentIsolationProvider[] providers) =>
        new(providers, new FixedTimeProvider(Now));

    private static IsolationSelectionRequest CreateRequest(AgentTrustLevel trustLevel) => new(
        trustLevel,
        new IsolationCapabilityRequirements(IsolationAssurance.None),
        "sha256:guest",
        "1.0");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeProvider : IAgentIsolationProvider
    {
        private readonly bool _available;
        private readonly IsolationProviderCertification? _certification;

        public FakeProvider(
            string id,
            IsolationAssurance assurance,
            bool certified,
            bool available = true,
            string certifiedImageDigest = "sha256:guest")
        {
            _available = available;
            Descriptor = new IsolationProviderDescriptor(
                id,
                id,
                "1.0.0",
                "windows",
                "x64",
                100,
                Capabilities(assurance));
            if (certified)
            {
                _certification = new IsolationProviderCertification(
                    id,
                    "1.0.0",
                    "windows",
                    "x64",
                    certifiedImageDigest,
                    "1.0",
                    "1.0",
                    "sha256:evidence",
                    Now.AddDays(-1));
            }
        }

        public IsolationProviderDescriptor Descriptor { get; }
        public int ProbeCount { get; private set; }

        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            return Task.FromResult(new IsolationProviderProbeResult(
                Descriptor,
                _available,
                _available ? null : "unavailable",
                _certification));
        }

        public Task<IsolationWorkloadHandle> CreateAsync(IsolationWorkloadSpec workload, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static IsolationProviderCapabilities Capabilities(IsolationAssurance assurance) => new(
            assurance,
            UsesDedicatedKernel: assurance >= IsolationAssurance.HardwareVirtualMachine,
            SupportsBrokerSocket: true,
            SupportsReadOnlyBaseDisk: true,
            SupportsReadOnlyArtifact: true,
            SupportsEphemeralWritableDisk: true,
            SupportsCpuLimits: true,
            SupportsMemoryLimits: true,
            SupportsDiskLimits: true,
            SupportsProcessLimits: true,
            SupportsNoNetworkDevice: assurance >= IsolationAssurance.HardwareVirtualMachine,
            SupportsSecureBoot: false,
            SupportsMeasuredOrVerifiedBoot: false);
    }
}
