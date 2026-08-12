using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.LocalRpc;
using CSweet.AgentRuntime.Protocol;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CSweet.UnitTests;

public sealed class RuntimeHostRpcIntegrationTests
{
    [Fact]
    public async Task ProbeFailsClosedWhenProviderHasNoGuestChannelConnector()
    {
        var descriptor = Descriptor();
        var dispatcher = new RuntimeHostRequestDispatcher(
            [new BackendAdapter(new InMemoryAgentIsolationProvider(descriptor))], []);
        var request = new RuntimeHostEnvelope
        {
            ProtocolVersion = "1.0",
            RequestId = Guid.NewGuid().ToString("D"),
            ProbeRequest = new ProbeRequest { ProviderId = descriptor.ProviderId }
        };
        var responses = new List<RuntimeHostEnvelope>();

        await foreach (var response in dispatcher.DispatchAsync(request)) responses.Add(response);

        var probe = Assert.Single(responses).ProbeResponse;
        Assert.False(probe.Available);
        Assert.Contains("guest-channel connector", probe.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(probe.CertificationJson);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsPipeSecurity_GrantsExactDuplexClientConnectionRights()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var factory = typeof(RuntimeHostRpcServer).GetMethod(
            "CreateWindowsPipeSecurity",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var security = (PipeSecurity)factory.Invoke(null, [sid.Value])!;
        var clientRule = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(sid) &&
                            rule.PipeAccessRights == (PipeAccessRights.ReadWrite |
                                                      PipeAccessRights.Synchronize |
                                                      PipeAccessRights.CreateNewInstance));

        Assert.Equal(AccessControlType.Allow, clientRule.AccessControlType);
    }

    [Fact]
    public async Task ClientAndServer_AuthenticateAndDispatchTypedLifecycle()
    {
        var descriptor = Descriptor();
        var backend = new InMemoryAgentIsolationProvider(descriptor);
        var endpoint = new RuntimeHostEndpointOptions
        {
            NamedPipeName = $"csweet-runtime-test-{Guid.NewGuid():N}",
            UnixSocketPath = Path.Combine(Path.GetTempPath(), $"csweet-runtime-test-{Guid.NewGuid():N}.sock"),
            ConnectTimeoutSeconds = 5
        };
        if (OperatingSystem.IsWindows())
            endpoint.AllowedClientSid = WindowsIdentity.GetCurrent().User?.Value;
        var authentication = new RuntimeHostAuthenticationOptions
        {
            KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(new byte[32])
        };
        var serverAuthenticator = new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System);
        var clientAuthenticator = new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System);
        var server = new RuntimeHostRpcServer(
            endpoint,
            serverAuthenticator,
            new RuntimeHostRequestDispatcher([new BackendAdapter(backend)], [new TestGuestConnector(descriptor.ProviderId)]),
            [new TestGuestConnector(descriptor.ProviderId)]);
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        await Task.Delay(100);
        var client = new RuntimeHostProviderClient(descriptor, endpoint, clientAuthenticator);
        var workload = Runtime();

        var handle = await client.CreateAsync(workload);
        await client.StartAsync(handle);
        Assert.Equal(IsolationWorkloadState.Running, (await client.InspectAsync(handle))!.State);
        await client.StopAsync(handle, TimeSpan.Zero);
        await client.DestroyAsync(handle);
        Assert.Null(await client.InspectAsync(handle));

        stop.Cancel();
        try { await serverTask; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task BackendFailureReturnsCorrelatedTypedErrorInsteadOfClosingPipe()
    {
        var descriptor = Descriptor();
        var endpoint = new RuntimeHostEndpointOptions
        {
            NamedPipeName = $"csweet-runtime-test-{Guid.NewGuid():N}",
            UnixSocketPath = Path.Combine(Path.GetTempPath(), $"csweet-runtime-test-{Guid.NewGuid():N}.sock"),
            ConnectTimeoutSeconds = 5
        };
        if (OperatingSystem.IsWindows())
            endpoint.AllowedClientSid = WindowsIdentity.GetCurrent().User?.Value;
        var authentication = new RuntimeHostAuthenticationOptions
        {
            KeyId = "test",
            SharedKeyBase64 = Convert.ToBase64String(new byte[32])
        };
        var server = new RuntimeHostRpcServer(
            endpoint,
            new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System),
            new RuntimeHostRequestDispatcher([new FailingBackend(descriptor)], [new TestGuestConnector(descriptor.ProviderId)]),
            [new TestGuestConnector(descriptor.ProviderId)]);
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        await Task.Delay(100);
        var client = new RuntimeHostProviderClient(
            descriptor,
            endpoint,
            new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System));

        var exception = await Assert.ThrowsAsync<IsolationUnavailableException>(() =>
            client.CreateAsync(Runtime()));

        Assert.Contains("provider-create-failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Diagnostic request:", exception.Message, StringComparison.Ordinal);
        stop.Cancel();
        try { await serverTask; }
        catch (OperationCanceledException) { }
    }

    private static IsolationProviderDescriptor Descriptor() => new(
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
            false));

    private static RuntimeWorkloadSpec Runtime()
    {
        var guest = "sha256:" + new string('a', 64);
        var artifact = "sha256:" + new string('b', 64);
        return new RuntimeWorkloadSpec(
            Guid.NewGuid(),
            new GuestImageReference("runtime", "1.0", guest, "linux", "x64"),
            new IsolationResourceLimits(1, 100, 512, 512, 100, 1024, TimeSpan.FromMinutes(1)),
            new BrokerChannelLease(Guid.NewGuid(), "1.0", "a-sufficiently-long-boot-token", guest, artifact, DateTimeOffset.UtcNow.AddMinutes(1)),
            new AgentArtifactReference(artifact, "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            ["/app/agent"]);
    }

    private sealed class BackendAdapter(InMemoryAgentIsolationProvider inner) : IPlatformIsolationBackend
    {
        public IsolationProviderDescriptor Descriptor => inner.Descriptor;
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => inner.ProbeAsync(cancellationToken);
        public Task<IsolationWorkloadHandle> CreateAsync(IsolationWorkloadSpec workload, CancellationToken cancellationToken = default) => inner.CreateAsync(workload, cancellationToken);
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.StartAsync(handle, cancellationToken);
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.InspectAsync(handle, cancellationToken);
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => inner.StopAsync(handle, gracePeriod, cancellationToken);
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => inner.DestroyAsync(handle, cancellationToken);
        public IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => inner.StreamLogsAsync(handle, maximumBytes, cancellationToken);
    }

    private sealed class TestGuestConnector(string providerId) : IPlatformGuestChannelConnector
    {
        public string ProviderId { get; } = providerId;
        public Task<Stream> OpenGuestChannelAsync(
            IsolationWorkloadHandle handle,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream>(Stream.Null);
    }

    private sealed class FailingBackend(IsolationProviderDescriptor descriptor) : IPlatformIsolationBackend
    {
        public IsolationProviderDescriptor Descriptor { get; } = descriptor;
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsolationProviderProbeResult(Descriptor, true, null, null));
        public Task<IsolationWorkloadHandle> CreateAsync(
            IsolationWorkloadSpec workload,
            CancellationToken cancellationToken = default) =>
            throw new IOException("The test backend could not create a VM.");
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(
            IsolationWorkloadHandle handle,
            int maximumBytes,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
