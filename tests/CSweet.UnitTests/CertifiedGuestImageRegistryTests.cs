using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;

namespace CSweet.UnitTests;

public sealed class CertifiedGuestImageRegistryTests
{
    [Fact]
    public async Task ResolveAsync_UsesActiveProviderCertificationWhenDigestAndVersionAreNotConfigured()
    {
        var certification = Certification("sha256:" + new string('a', 64), "windows-hyperv-v1");
        var registry = new CertifiedGuestImageRegistry(new RecordingSelector(certification));

        var image = await registry.ResolveAsync(new GuestImageResolutionRequest(
            "csweet-builder-base", "", "linux", "x64",
            AgentTrustLevel.UntrustedRepository, "1.0"));

        Assert.Equal(certification.GuestImageDigest, image.Digest);
        Assert.Equal(certification.CertificationSuiteVersion, image.Version);
    }

    [Fact]
    public async Task ResolveAsync_PreservesOptionalConfiguredDigestPin()
    {
        var digest = "sha256:" + new string('b', 64);
        var selector = new RecordingSelector(Certification(digest, "windows-hyperv-v1"));
        var registry = new CertifiedGuestImageRegistry(selector);

        await registry.ResolveAsync(new GuestImageResolutionRequest(
            "csweet-runtime-base", "release-1", "linux", "x64",
            AgentTrustLevel.UntrustedRepository, "1.0", ExpectedDigest: digest));

        Assert.Equal(digest, selector.Request!.GuestImageDigest);
    }

    [Fact]
    public async Task ResolveAsync_RejectsCertifiedImageFromAStaleGuestContract()
    {
        var registry = new CertifiedGuestImageRegistry(new RecordingSelector(
            Certification("sha256:" + new string('d', 64), "windows-hyperv-v1")));

        var exception = await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            registry.ResolveAsync(new GuestImageResolutionRequest(
                "csweet-builder-base", "release-2", "linux", "x64",
                AgentTrustLevel.UntrustedRepository, "1.0",
                RequiredCertificationSuiteVersion: "windows-hyperv-v2")));

        Assert.Contains("out of date", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("installed: windows-hyperv-v1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("required: windows-hyperv-v2", exception.Message, StringComparison.Ordinal);
    }

    private static IsolationProviderCertification Certification(string digest, string suite) => new(
        "hyperv-gen2", "1.0.0", "windows", "x64", digest, "1.0", suite,
        "sha256:" + new string('c', 64), DateTimeOffset.UtcNow.AddMinutes(-1));

    private sealed class RecordingSelector(IsolationProviderCertification certification) : IAgentIsolationProviderSelector
    {
        public IsolationSelectionRequest? Request { get; private set; }

        public Task<IsolationProviderSelection> SelectAsync(
            IsolationSelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var provider = new FakeProvider();
            return Task.FromResult(new IsolationProviderSelection(
                provider,
                new IsolationProviderProbeResult(provider.Descriptor, true, null, certification)));
        }
    }

    private sealed class FakeProvider : IAgentIsolationProvider
    {
        public IsolationProviderDescriptor Descriptor { get; } = IsolationProviderCatalog.HyperV("x64");
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
    }
}
