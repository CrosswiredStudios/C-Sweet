using System.Net;
using System.Text;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Guest;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class BuilderGuestWorkflowTests
{
    [Theory]
    [InlineData(0, "/build/fetch", "build.fetch")]
    [InlineData(0, "/build/artifact", "build.artifact")]
    [InlineData(0, "/build/progress", "build.progress")]
    [InlineData(1, "/mcp", "mcp.runtime")]
    public void GuestBrokerRoutesOnlyWorkloadSpecificEndpoints(int kind, string path, string expected) =>
        Assert.Equal(expected, GuestBrokerSession.PurposeFor(kind, path));

    [Theory]
    [InlineData(0, "/mcp")]
    [InlineData(1, "/build/fetch")]
    public void GuestBrokerRejectsCrossProfileEndpoints(int kind, string path) =>
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(kind, path));

    [Fact]
    public void BuilderOptionsAcceptOnlySupportedLinuxTargetFrameworks()
    {
        var options = global::BuilderOptions.Parse(Arguments("net10.0"));

        Assert.Equal("net10.0", options.TargetFramework);
        Assert.Throws<InvalidDataException>(() => global::BuilderOptions.Parse(Arguments("net10.0-windows")));
    }

    [Fact]
    public void NuGetProxyPreservesPathsAppendedToRewrittenServiceResources()
    {
        var upstream = "https://api.nuget.org/v3-flatcontainer/";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(upstream))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var decoded = global::NuGetLoopbackProxy.DecodeUpstream(
            $"/upstream/{token}/example.package/index.json");

        Assert.Equal("https://api.nuget.org/v3-flatcontainer/example.package/index.json", decoded.AbsoluteUri);
    }

    [Fact]
    public async Task NuGetProxyUsesEphemeralTrustedHttpsForRepositorySignatureMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"csweet-nuget-proxy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var broker = new global::BuilderBrokerClient(Path.Combine(root, "unused.sock"));
            await using var proxy = await global::NuGetLoopbackProxy.StartAsync(broker, root);

            Assert.StartsWith("https://localhost:", proxy.ServiceIndexUrl, StringComparison.Ordinal);
            Assert.True(File.Exists(proxy.TrustCertificatePath));
            Assert.Contains("BEGIN CERTIFICATE", await File.ReadAllTextAsync(proxy.TrustCertificatePath),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NuGetConfigurationPinsPublishedRepositorySignersWhileUsingTheLocalHttpsProxy()
    {
        const string fingerprint = "1f4b311d9acc115c8dc8018b5a49e00fce6da8e2855f9f014ca6f34570bc482d";
        var trusted = global::NuGetTrustedRepository.Parse(
            "https://api.nuget.org/v3/index.json",
            "{\"allRepositorySigned\":true,\"signingCertificates\":[{\"fingerprints\":{\"2.16.840.1.101.3.4.2.1\":\"" +
            fingerprint + "\"}}]}");

        var configuration = global::BuilderProgram.NuGetConfiguration(
            "https://localhost:43123/v3/index.json",
            trusted);

        Assert.Contains("signatureValidationMode\" value=\"require", configuration, StringComparison.Ordinal);
        Assert.Contains("serviceIndex=\"https://api.nuget.org/v3/index.json\"", configuration, StringComparison.Ordinal);
        Assert.Contains($"fingerprint=\"{fingerprint}\"", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("allowInsecureConnections", configuration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void BuildBrokerRejectsNonPublicResolvedAddresses(string value, bool expected) =>
        Assert.Equal(expected, BuilderBrokerOperationHandler.IsPublicAddress(IPAddress.Parse(value)));

    [Fact]
    public async Task VmBuildRunsAuthenticatedGuestSessionAndAlwaysDestroysVm()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"csweet-builder-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logRoot);
        try
        {
        var digest = "sha256:" + new string('a', 64);
        var guest = new GuestImageReference("csweet-builder-base", "suite-v2", digest, "linux", "x64");
        var provider = new RecordingProvider();
        var session = new RecordingSessionCoordinator();
        var resultStore = new ImmediateResultStore(new BuilderArtifactResult(
            Guid.Empty,
            new AgentArtifactReference("sha256:" + new string('b', 64), "signature", "1.0", "linux", "x64"),
            "artifact:sha256:" + new string('b', 64)));
        var executor = new VmAgentBuildExecutor(
            new FixedSelector(provider, digest),
            new FixedGuestImageRegistry(guest),
            session,
            resultStore,
            Options.Create(new AgentRuntimeManagerOptions { BuildLogStorePath = logRoot }));
        var request = new AgentBuildExecutionRequest(
            Guid.NewGuid(), Guid.NewGuid(), "https://github.com/example/agent.git", new string('c', 40),
            "src/Agent/Agent.csproj", "net10.0", "dotnet-publish-v1", 600, 512, 50, 128, 128, 8);
        resultStore.WorkloadId = request.BuildJobId;
        var progress = new RecordingProgressReporter();
        var workspace = await executor.CloneAsync(request, progress);

        var result = await executor.BuildAsync(request, workspace, progress);

        Assert.True(provider.Created);
        Assert.True(provider.Started);
        Assert.True(provider.Destroyed);
        Assert.True(session.Started);
        Assert.Contains(progress.Updates, update =>
            update.StepKey == AgentBuildStepKeys.Isolate &&
            update.Status == AgentBuildStepStatuses.InProgress &&
            update.Detail?.Contains("certified builder image", StringComparison.Ordinal) == true);
        Assert.Contains(progress.Updates, update =>
            update.StepKey == AgentBuildStepKeys.Isolate &&
            update.Status == AgentBuildStepStatuses.InProgress &&
            update.Detail?.Contains("Starting the disposable builder guest", StringComparison.Ordinal) == true);
        Assert.Equal(digest, provider.Workload!.GuestImage.Digest);
        Assert.Equal(new string('b', 64), result.PackageDigest);
        Assert.True(File.Exists(workspace.LogPath));
        var log = await File.ReadAllTextAsync(workspace.LogPath);
        Assert.Contains(request.BuildJobId.ToString("D"), log, StringComparison.Ordinal);
        Assert.Contains("Destroyed disposable provider instance", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeInspectionSurfacesGuestProcessExitEvenWhileVmIsStillRunning()
    {
        var provider = new RecordingProvider();
        var handle = new IsolationWorkloadHandle(provider.Descriptor.ProviderId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), IsolationWorkloadKind.Runtime);
        var guestSessions = new ExitedRuntimeGuestCoordinator(new AgentGuestSessionOutcome(
            1, "process-exited", "The agent could not load its entrypoint."));
        var runner = new IsolationAgentWorkloadRunner(
            new FixedSelector(provider, "sha256:" + new string('a', 64)),
            [provider],
            guestSessions,
            new NoOpArtifactMediaStore());

        var status = await runner.InspectAsync(handle);

        Assert.NotNull(status);
        Assert.Equal(IsolationWorkloadState.Failed, status.State);
        Assert.Equal(1, status.ExitCode);
        Assert.Contains("entrypoint", status.SanitizedError, StringComparison.Ordinal);
        Assert.Equal(0, provider.InspectCount);
    }

    [Fact]
    public async Task RuntimeLogsIncludeGuestDiagnosticWhenProviderHasNoOutput()
    {
        var provider = new RecordingProvider();
        var handle = new IsolationWorkloadHandle(
            provider.Descriptor.ProviderId,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            IsolationWorkloadKind.Runtime);
        var guestSessions = new ExitedRuntimeGuestCoordinator(new AgentGuestSessionOutcome(
            0, "process-exited", "The agent stopped after startup."));
        var runner = new IsolationAgentWorkloadRunner(
            new FixedSelector(provider, "sha256:" + new string('a', 64)),
            [provider],
            guestSessions,
            new NoOpArtifactMediaStore());

        var logs = await runner.GetLogsAsync(handle, 1024);

        Assert.Contains("stopped after startup", logs, StringComparison.Ordinal);
    }

    private static string[] Arguments(string targetFramework) =>
    [
        "--repository", "https://github.com/example/agent.git",
        "--commit", new string('a', 40),
        "--project", "src/Agent/Agent.csproj",
        "--maximum-repository-bytes", "1048576",
        "--maximum-artifact-bytes", "1048576",
        "--broker-socket", "/run/csweet/broker.sock",
        "--target-framework", targetFramework
    ];

    private sealed class RecordingProvider : IAgentIsolationProvider
    {
        public IsolationProviderDescriptor Descriptor { get; } = IsolationProviderCatalog.HyperV();
        public bool Created { get; private set; }
        public bool Started { get; private set; }
        public bool Destroyed { get; private set; }
        public int InspectCount { get; private set; }
        public IsolationWorkloadSpec? Workload { get; private set; }
        public Task<IsolationProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IsolationWorkloadHandle> CreateAsync(IsolationWorkloadSpec workload, CancellationToken cancellationToken = default)
        {
            Created = true;
            Workload = workload;
            return Task.FromResult(new IsolationWorkloadHandle(Descriptor.ProviderId, workload.WorkloadId, Guid.NewGuid().ToString("N"), workload.Kind));
        }
        public Task StartAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Started = true; return Task.CompletedTask; }
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
        {
            InspectCount++;
            return Task.FromResult<IsolationWorkloadStatus?>(new IsolationWorkloadStatus(
                handle, IsolationWorkloadState.Running, IsolationTerminationReason.None, null,
                DateTimeOffset.UtcNow, null, null, null));
        }
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Destroyed = true; return Task.CompletedTask; }
        public async IAsyncEnumerable<IsolationLogChunk> StreamLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }

    private sealed class FixedSelector(IAgentIsolationProvider provider, string digest) : IAgentIsolationProviderSelector
    {
        public Task<IsolationProviderSelection> SelectAsync(IsolationSelectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsolationProviderSelection(provider, new IsolationProviderProbeResult(
                provider.Descriptor, true, null, new IsolationProviderCertification(
                    provider.Descriptor.ProviderId, provider.Descriptor.ProviderVersion,
                    provider.Descriptor.HostOperatingSystem, provider.Descriptor.HostArchitecture,
                    digest, "1.0", "suite-v2", "sha256:" + new string('d', 64), DateTimeOffset.UtcNow))));
    }

    private sealed class FixedGuestImageRegistry(GuestImageReference image) : IGuestImageRegistry
    {
        public Task<GuestImageReference> ResolveAsync(GuestImageResolutionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(image);
    }

    private sealed class RecordingSessionCoordinator : IBuilderGuestSessionCoordinator
    {
        public bool Started { get; private set; }
        public Task<IBuilderGuestSession> StartAsync(IsolationWorkloadHandle handle, BuilderWorkloadSpec workload, AgentBuildExecutionRequest request, IAgentBuildProgressReporter progress, CancellationToken cancellationToken = default)
        { Started = true; return Task.FromResult<IBuilderGuestSession>(new CompletedSession()); }
    }

    private sealed class CompletedSession : IBuilderGuestSession
    {
        public Task Completion => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediateResultStore(BuilderArtifactResult template) : IBuilderArtifactResultStore
    {
        public Guid WorkloadId { get; set; }
        public Task<BuilderArtifactResult> WaitAsync(Guid workloadId, CancellationToken cancellationToken = default) =>
            Task.FromResult(template with { WorkloadId = WorkloadId });
    }

    private sealed class NoOpProgressReporter : IAgentBuildProgressReporter
    {
        public Task ReportAsync(AgentBuildProgressUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingProgressReporter : IAgentBuildProgressReporter
    {
        public List<AgentBuildProgressUpdate> Updates { get; } = [];

        public Task ReportAsync(
            AgentBuildProgressUpdate update,
            CancellationToken cancellationToken = default)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }

    private sealed class ExitedRuntimeGuestCoordinator(AgentGuestSessionOutcome outcome) : IAgentGuestSessionCoordinator
    {
        public Task StartAsync(IsolationWorkloadHandle handle, RuntimeWorkloadSpec workload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public AgentGuestSessionOutcome? GetOutcome(IsolationWorkloadHandle handle) => outcome;
        public string? GetLogs(IsolationWorkloadHandle handle, int maximumBytes) => null;
    }

    private sealed class NoOpArtifactMediaStore : IAgentArtifactMediaStore
    {
        public Task EnsureReadOnlyMediaAsync(string digest, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
