using System.Security.Cryptography;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Artifacts;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.LocalRpc;
using CSweet.AgentRuntime.Protocol;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("The installed RuntimeHost system test requires Windows.");

var arguments = SystemTestArguments.Parse(args);
var outputRoot = Path.GetFullPath(arguments.OutputRoot);
Directory.CreateDirectory(outputRoot);
var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
var runtimeData = Path.Combine(commonData, "CSweet", "AgentRuntime");
var keyPath = Path.Combine(runtimeData, "runtime-host.key");
var endpoint = new RuntimeHostEndpointOptions();
var authentication = new RuntimeHostAuthenticationOptions
{
    KeyId = "control-plane",
    SharedKeyFilePath = keyPath
};
authentication.LoadSharedKeyFileIfNeeded(keyPath);
var provider = new RuntimeHostProviderClient(
    IsolationProviderCatalog.HyperV(),
    endpoint,
    new RuntimeHostRequestAuthenticator(authentication, TimeProvider.System));
var providers = new IAgentIsolationProvider[] { provider };
var selector = new FailClosedIsolationProviderSelector(providers, TimeProvider.System);
var guestImages = new CertifiedGuestImageRegistry(selector);

Console.WriteLine("Probing the installed authenticated RuntimeHost...");
var probe = await provider.ProbeAsync();
if (!probe.IsAvailable || probe.Certification is null)
    throw new InvalidOperationException($"RuntimeHost is unavailable: {probe.UnavailableReason ?? "no reason returned"}");
Console.WriteLine($"RuntimeHost certification {probe.Certification.CertificationSuiteVersion} is active.");

var artifactOptions = new ArtifactStoreOptions { RootPath = Path.Combine(outputRoot, "artifacts") };
var artifactStore = new FileSystemAgentArtifactStore(
    artifactOptions,
    new HmacAgentArtifactSigner(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
var results = new InMemoryBuilderArtifactResultStore();
var transport = new WindowsHyperVSocketTransport(new HyperVSocketTransportOptions
{
    ConnectTimeoutSeconds = 180
});
var builderSessions = new BuilderGuestSessionCoordinator(
    transport,
    artifactStore,
    results,
    artifactOptions,
    TimeProvider.System,
    NullLogger<BuilderGuestSessionCoordinator>.Instance);
var runtimeOptions = new AgentRuntimeManagerOptions
{
    PreferredIsolationProviderId = provider.Descriptor.ProviderId,
    RequiredCertificationSuiteVersion = AgentRuntimeManagerOptions.CurrentDevelopmentCertificationSuiteVersion,
    BuildLogStorePath = Path.Combine(outputRoot, "logs")
};
var executor = new VmAgentBuildExecutor(
    selector,
    guestImages,
    builderSessions,
    results,
    Options.Create(runtimeOptions));
var buildRequest = new AgentBuildExecutionRequest(
    Guid.NewGuid(),
    Guid.NewGuid(),
    arguments.RepositoryUrl,
    arguments.CommitSha,
    arguments.ProjectPath,
    "net10.0",
    "dotnet-publish-v1",
    900,
    2048,
    100,
    256,
    512,
    16);
var progress = new ConsoleProgressReporter();
var workspace = await executor.CloneAsync(buildRequest, progress);
Console.WriteLine($"Building {arguments.RepositoryUrl}@{arguments.CommitSha} inside a disposable Hyper-V VM...");
var build = await executor.BuildAsync(buildRequest, workspace, progress);
Console.WriteLine($"Immutable package built: sha256:{build.PackageDigest}");

var artifact = new AgentArtifactReference(
    $"sha256:{build.PackageDigest}",
    build.ArtifactSignature ?? throw new InvalidDataException("The builder did not sign the artifact."),
    build.ArtifactFormatVersion,
    build.ArtifactOperatingSystem,
    build.ArtifactArchitecture);
var media = new FileSystemAgentArtifactMediaStore(
    new ArtifactMediaOptions { RootPath = Path.Combine(runtimeData, "artifact-media") },
    artifactStore);
var operationHandler = new SystemTestOperationHandler(Path.Combine(outputRoot, "runtime-broker.log"));
var runtimeSessions = new HyperVGuestSessionCoordinator(
    transport,
    operationHandler,
    TimeProvider.System,
    NullLogger<HyperVGuestSessionCoordinator>.Instance);
var runner = new IsolationAgentWorkloadRunner(selector, providers, runtimeSessions, media);
var guest = await guestImages.ResolveAsync(new GuestImageResolutionRequest(
    runtimeOptions.RuntimeGuestImageId,
    runtimeOptions.RuntimeGuestImageVersion,
    runtimeOptions.RuntimeGuestOperatingSystem,
    runtimeOptions.RuntimeGuestArchitecture,
    AgentTrustLevel.UntrustedRepository,
    "1.0",
    provider.Descriptor.ProviderId,
    runtimeOptions.RuntimeGuestImageDigest,
    runtimeOptions.RequiredCertificationSuiteVersion));
var workloadId = Guid.NewGuid();
var handle = default(IsolationWorkloadHandle);
try
{
    var lease = new BrokerChannelLease(
        Guid.NewGuid(),
        "1.0",
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        guest.Digest,
        artifact.Digest,
        DateTimeOffset.UtcNow.AddMinutes(5));
    var runtimeWorkload = new RuntimeWorkloadSpec(
        workloadId,
        guest,
        new IsolationResourceLimits(1, 100, 1024, 1024, 128, 4 * 1024 * 1024, TimeSpan.FromMinutes(3)),
        lease,
        artifact,
        new RuntimeAgentIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()),
        [Path.GetFileNameWithoutExtension(arguments.ProjectPath)]);
    Console.WriteLine("Starting the built agent inside a separate disposable Hyper-V VM...");
    handle = await runner.CreateAndStartAsync(
        runtimeWorkload,
        AgentTrustLevel.UntrustedRepository,
        provider.Descriptor.ProviderId);
    using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    while (!operationHandler.RuntimeReady.IsCompleted)
    {
        var startupStatus = await runner.InspectAsync(handle, startupTimeout.Token);
        if (startupStatus?.State is not IsolationWorkloadState.Running)
        {
            var startupLogs = await runner.GetLogsAsync(handle, 1024 * 1024, CancellationToken.None);
            throw new InvalidOperationException(
                $"The built agent exited before completing its MCP startup handshake " +
                $"(state: {startupStatus?.State.ToString() ?? "missing"}, " +
                $"error: {startupStatus?.ErrorCode ?? "none"}, " +
                $"detail: {startupStatus?.SanitizedError ?? "none"}). Output: {startupLogs}");
        }
        await Task.Delay(TimeSpan.FromMilliseconds(500), startupTimeout.Token);
    }
    await operationHandler.RuntimeReady;
    await Task.Delay(TimeSpan.FromSeconds(2));
    var status = await runner.InspectAsync(handle);
    if (status?.State != IsolationWorkloadState.Running)
    {
        var logs = await runner.GetLogsAsync(handle, 1024 * 1024);
        throw new InvalidOperationException(
            $"The built agent did not remain running (state: {status?.State.ToString() ?? "missing"}, " +
            $"error: {status?.ErrorCode ?? "none"}, detail: {status?.SanitizedError ?? "none"}). Output: {logs}");
    }
    Console.WriteLine(
        $"Agent workload {workloadId:D} initialized its MCP session, entered the work loop, " +
        $"and is running in provider instance {handle.ProviderInstanceId}.");
}
finally
{
    if (handle is not null)
    {
        try { await runner.StopAsync(handle, TimeSpan.FromSeconds(5)); } catch { }
        try { await runner.DestroyAsync(handle); } catch { }
    }
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = true,
    repository = arguments.RepositoryUrl,
    commit = arguments.CommitSha,
    buildJobId = buildRequest.BuildJobId,
    artifactDigest = artifact.Digest,
    buildLog = build.LogPath,
    runtimeBrokerLog = operationHandler.LogPath,
    runtimeWorkloadId = workloadId
}));

internal sealed class ConsoleProgressReporter : IAgentBuildProgressReporter
{
    public Task ReportAsync(AgentBuildProgressUpdate update, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] {update.StepKey}: {update.Status} - {update.Detail ?? update.Error}");
        return Task.CompletedTask;
    }
}

internal sealed class SystemTestOperationHandler : IAgentBrokerOperationHandler
{
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _claimObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _logLock = new(1, 1);

    public SystemTestOperationHandler(string logPath)
    {
        LogPath = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
    }

    public string LogPath { get; }
    public Task RuntimeReady => Task.WhenAll(_initialized.Task, _claimObserved.Task);

    public async Task<BrokerOperationResult> HandleAsync(
        BrokerOperationContext request,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        var requestId = root.GetProperty("id").GetString() ?? request.RequestId;
        var method = root.GetProperty("method").GetString() ?? string.Empty;
        await AppendLogAsync(request, method, cancellationToken);

        object result;
        IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (method)
        {
            case "initialize":
                var sessionId = Guid.NewGuid().ToString("N");
                headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Mcp-Session-Id"] = sessionId
                };
                result = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    serverInfo = new { name = "csweet-windows-system-test", version = "1.0" },
                    _meta = new
                    {
                        csweet = new
                        {
                            accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                            sessionId,
                            grantRevision = 1L
                        }
                    }
                };
                _initialized.TrySetResult();
                break;
            case "csweet/work/claim":
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                result = new { work = (object?)null };
                _claimObserved.TrySetResult();
                break;
            default:
                result = new { };
                break;
        }

        var response = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = requestId, result });
        return new BrokerOperationResult(200, headers, response);
    }

    private async Task AppendLogAsync(
        BrokerOperationContext request,
        string method,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(new
        {
            occurredAt = DateTimeOffset.UtcNow,
            request.WorkloadId,
            request.InstallationId,
            request.RequestId,
            request.Purpose,
            request.Method,
            request.Path,
            mcpMethod = method
        }) + Environment.NewLine;
        await _logLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(LogPath, line, cancellationToken);
        }
        finally
        {
            _logLock.Release();
        }
    }
}

internal sealed record SystemTestArguments(
    string RepositoryUrl,
    string CommitSha,
    string ProjectPath,
    string OutputRoot)
{
    public static SystemTestArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw Usage();
            values[args[index][2..]] = args[index + 1];
        }
        var repository = Required(values, "repository");
        var commit = Required(values, "commit");
        var project = Required(values, "project");
        var output = Required(values, "output");
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            commit.Length != 40 || commit.Any(character => !Uri.IsHexDigit(character)) ||
            Path.IsPathFullyQualified(project) || project.Contains("..", StringComparison.Ordinal))
            throw Usage();
        return new SystemTestArguments(repository, commit.ToLowerInvariant(), project.Replace('\\', '/'), output);
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw Usage();

    private static ArgumentException Usage() => new(
        "Usage: --repository <https-url> --commit <40-hex-sha> --project <relative-csproj> --output <directory>");
}
