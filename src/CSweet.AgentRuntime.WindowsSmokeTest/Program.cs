using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.Firecracker;
using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.Protocol;
using Microsoft.Win32;

var arguments = SmokeArguments.Parse(args);
if (arguments.Provider == "hyperv" && !OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("The Hyper-V smoke test requires Windows.");
if (arguments.Provider == "firecracker" && !OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("The Firecracker smoke test requires Linux.");
if (arguments.Provider == "hyperv" &&
    !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Run the Hyper-V isolation smoke test from an administrator prompt.");
if (arguments.Provider == "firecracker" && Environment.UserName != "root")
    throw new InvalidOperationException("Run the Firecracker isolation smoke test as root.");

var root = arguments.ValidatedOutputRoot();
var mediaRoot = Path.Combine(root, "artifact-media");
var helperRoot = Path.Combine(root, "hyperv-helper-data");
Directory.CreateDirectory(mediaRoot);
Directory.CreateDirectory(helperRoot);
Environment.SetEnvironmentVariable("CSWEET_ARTIFACT_MEDIA_ROOT", mediaRoot);
if (arguments.Provider == "hyperv")
    Environment.SetEnvironmentVariable("CSWEET_HYPERV_DATA_ROOT", helperRoot);
else
    Environment.SetEnvironmentVariable("CSWEET_FIRECRACKER_DATA_ROOT", helperRoot);
var socketOptions = new HyperVSocketTransportOptions { ConnectTimeoutSeconds = 120 };
var serviceId = socketOptions.ServiceId;
if (arguments.Provider == "hyperv")
{
    Environment.SetEnvironmentVariable("CSWEET_HYPERV_BROKER_SERVICE_ID", serviceId.ToString("D"));
    RegisterHyperVSocket(serviceId);
}

var descriptor = arguments.Provider == "hyperv"
    ? IsolationProviderCatalog.HyperV(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant())
    : IsolationProviderCatalog.Firecracker(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
var guestImage = arguments.RequireFile(arguments.GuestImagePath,
    arguments.Provider == "hyperv" ? ".vhdx" : ".ext4");
var helper = arguments.RequireFile(arguments.HelperExecutablePath,
    arguments.Provider == "hyperv" ? ".exe" : string.Empty);
var probe = arguments.RequireFile(arguments.ProbeExecutablePath, string.Empty);
var guestDigest = await DigestAsync(guestImage);
var guestArchitecture = RuntimeInformation.ProcessArchitecture switch
{
    Architecture.X64 => "x64",
    Architecture.Arm64 => "arm64",
    _ => throw new PlatformNotSupportedException("The certification host architecture is unsupported.")
};
var bundle = Path.Combine(root, "certification-probe.csab");
var artifactDigest = await CreateProbeBundleAsync(probe, bundle, guestArchitecture);
var artifactIso = Path.Combine(mediaRoot, $"{artifactDigest[7..]}.iso");
await using (var input = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read))
await using (var output = new FileStream(artifactIso, FileMode.Create, FileAccess.Write, FileShare.None))
    await SingleFileIso9660.WriteAsync(input, input.Length, output);
if (!await SingleFileIso9660.VerifyArtifactDigestAsync(artifactIso, artifactDigest))
    throw new InvalidDataException("The certification artifact ISO failed verification.");

var now = DateTimeOffset.UtcNow;
var workloadId = Guid.NewGuid();
var channelId = Guid.NewGuid();
var installationId = Guid.NewGuid();
var businessId = Guid.NewGuid();
var tickId = Guid.NewGuid();
var bootToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var expiresAt = now.AddMinutes(5);
var image = new GuestImageReference("csweet-hardware-vm-test", "1.0", guestDigest, "linux", guestArchitecture);
var workload = new RuntimeWorkloadSpec(
    workloadId,
    image,
    new IsolationResourceLimits(1, 100, 1024, 1024, 64, 1024 * 1024, TimeSpan.FromMinutes(3)),
    new BrokerChannelLease(channelId, "1.0", bootToken, guestDigest, artifactDigest, expiresAt),
    new AgentArtifactReference(artifactDigest, "smoke-test", "1.0", "linux", "x64"),
    new RuntimeAgentIdentity(installationId, businessId.ToString("D"), tickId),
    ["probe"]);
IsolationWorkloadHandle? handle = null;
SmokeGuestReport? certifiedReport = null;
Exception? cleanupFailure = null;
try
{
    var created = await InvokeHelperAsync(helper, "create", new PlatformHelperRequest
    {
        RuntimeWorkload = workload,
        GuestImagePath = guestImage,
        ArtifactImagePath = artifactIso
    });
    EnsureSuccess(created);
    if (!Guid.TryParseExact(created.ProviderInstanceId, "N", out var virtualMachineId))
        throw new InvalidDataException("The helper returned an invalid Hyper-V VM identifier.");
    handle = new IsolationWorkloadHandle(
        descriptor.ProviderId, workloadId,
        virtualMachineId.ToString("N"), IsolationWorkloadKind.Runtime);
    EnsureSuccess(await InvokeHelperAsync(helper, "start", new PlatformHelperRequest { Handle = handle }));

    var collector = new CertificationReportHandler();
    var grant = new AgentBrokerGrant(
        workloadId, channelId, installationId, guestDigest, artifactDigest, "1.0", bootToken, expiresAt,
        new HashSet<string>(StringComparer.Ordinal) { "mcp.runtime" },
        8, 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024);
    var boot = new GuestBootConfiguration
    {
        WorkloadId = workloadId.ToString("D"),
        ChannelId = channelId.ToString("D"),
        ProtocolVersion = "1.0",
        GuestImageDigest = guestDigest,
        ArtifactDigest = artifactDigest,
        BootToken = bootToken,
        LeaseExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
        ArtifactRoot = "/run/csweet/artifact/payload",
        WorkloadKind = (int)IsolationWorkloadKind.Runtime,
        InstallationId = installationId.ToString("D"),
        BusinessId = businessId.ToString("D"),
        TickId = tickId.ToString("D"),
        LocalBrokerSocketPath = "/run/csweet/broker.sock",
        WorkloadTokenPath = "/run/csweet/workload-token",
        MaximumFrameBytes = 16 * 1024 * 1024
    };
    var start = new StartCommand
    {
        WorkloadKind = (int)IsolationWorkloadKind.Runtime,
        MaximumLogBytes = 1024 * 1024
    };
    start.Entrypoint.Add("probe");
    var hostSession = new GuestBrokerHostSession(
        grant, collector, TimeProvider.System,
        bootConfiguration: boot, startCommand: start);
    await using var transport = await ConnectGuestAsync(
        arguments.Provider, helper, handle, socketOptions);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var sessionTask = hostSession.RunAsync(transport, transport, timeout.Token);
    var startupTask = hostSession.Started.WaitAsync(timeout.Token);
    var startupCompleted = await Task.WhenAny(startupTask, sessionTask).WaitAsync(timeout.Token);
    if (startupCompleted == sessionTask) await sessionTask;
    await startupTask;
    var completed = await Task.WhenAny(collector.Report, sessionTask).WaitAsync(timeout.Token);
    if (completed == sessionTask && !collector.Report.IsCompleted) await sessionTask;
    var report = await collector.Report.WaitAsync(timeout.Token);
    if (!report.Passed || report.Checks.Count == 0 || report.Checks.Any(check => !check.Value))
        throw new InvalidOperationException("The guest reported a failed isolation check: " +
            string.Join(", ", report.Checks.Where(check => !check.Value).Select(check => check.Key)));
    await sessionTask.WaitAsync(timeout.Token);
    certifiedReport = report;
}
finally
{
    if (handle is not null && arguments.PreserveFailedVirtualMachine && certifiedReport is null)
    {
        Console.Error.WriteLine(
            $"Diagnostic preservation requested. Provider VM id: {handle.ProviderInstanceId}; data root: {helperRoot}");
    }
    else if (handle is not null)
    {
        try
        {
            EnsureSuccess(await InvokeHelperAsync(helper, "stop",
                new PlatformHelperRequest { Handle = handle, GracePeriodSeconds = 5 }));
        }
        catch (Exception exception) { cleanupFailure = exception; }
        try
        {
            EnsureSuccess(await InvokeHelperAsync(helper, "destroy", new PlatformHelperRequest { Handle = handle }));
        }
        catch (Exception exception) { cleanupFailure ??= exception; }
    }
}

if (cleanupFailure is not null)
    throw new InvalidOperationException("The certification VM could not be destroyed cleanly.", cleanupFailure);
var validatedReport = certifiedReport ?? throw new InvalidOperationException("The guest did not produce certification evidence.");
var builderChecks = await RunBuilderSmokeAsync(
    helper,
    guestImage,
    guestDigest,
    root,
    socketOptions,
    arguments.Provider,
    descriptor,
    guestArchitecture);
if (builderChecks.Any(check => !check.Value))
    throw new InvalidOperationException("The builder certification probe failed: " +
        string.Join(", ", builderChecks.Where(check => !check.Value).Select(check => check.Key)));
var combinedChecks = validatedReport.Checks
    .Concat(builderChecks)
    .ToDictionary(check => check.Key, check => check.Value, StringComparer.Ordinal);
var evidence = new
{
    providerId = descriptor.ProviderId,
    providerVersion = descriptor.ProviderVersion,
    hostOperatingSystem = descriptor.HostOperatingSystem,
    hostArchitecture = descriptor.HostArchitecture,
    guestImageDigest = guestDigest,
    brokerProtocolVersion = "1.0",
    certificationSuiteVersion = validatedReport.Suite,
    certifiedAt = now,
    certificationExpiresAt = now.AddDays(7),
    checks = combinedChecks,
    guestOperatingSystem = validatedReport.GuestOperatingSystem,
    completedAt = validatedReport.CompletedAt
};
var evidencePath = Path.GetFullPath(arguments.EvidenceOutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, SmokeJson.Options));
Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = true,
    guestImageDigest = guestDigest,
    evidencePath,
    certificationSuiteVersion = validatedReport.Suite
}, SmokeJson.Options));

static async Task<IReadOnlyDictionary<string, bool>> RunBuilderSmokeAsync(
    string helper,
    string guestImage,
    string guestDigest,
    string root,
    HyperVSocketTransportOptions socketOptions,
    string provider,
    IsolationProviderDescriptor descriptor,
    string guestArchitecture)
{
    var workloadId = Guid.NewGuid();
    var channelId = Guid.NewGuid();
    var installationId = Guid.NewGuid();
    var bootToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(6);
    var image = new GuestImageReference("csweet-hardware-vm-test", "1.0", guestDigest, "linux", guestArchitecture);
    var workload = new BuilderWorkloadSpec(
        workloadId,
        image,
        new IsolationResourceLimits(1, 100, 4096, 1024, 128, 4 * 1024 * 1024, TimeSpan.FromMinutes(5)),
        new BrokerChannelLease(channelId, "1.0", bootToken, guestDigest, null, expiresAt),
        new RepositoryDescriptor(
            "https://github.com/csweet/certification-fixture.git",
            new string('a', 40),
            false,
            "dotnet-publish-v1",
            "1.0"),
        100 * 1024 * 1024);
    IsolationWorkloadHandle? handle = null;
    try
    {
        var created = await InvokeHelperAsync(helper, "create", new PlatformHelperRequest
        {
            BuilderWorkload = workload,
            GuestImagePath = guestImage
        });
        EnsureSuccess(created);
        if (!Guid.TryParseExact(created.ProviderInstanceId, "N", out var virtualMachineId))
            throw new InvalidDataException("The helper returned an invalid builder VM identifier.");
        handle = new IsolationWorkloadHandle(
            descriptor.ProviderId,
            workloadId,
            virtualMachineId.ToString("N"),
            IsolationWorkloadKind.Builder);
        EnsureSuccess(await InvokeHelperAsync(helper, "start", new PlatformHelperRequest { Handle = handle }));

        var handler = new BuilderCertificationHandler(CreateBuilderFixtureArchive());
        var grant = new AgentBrokerGrant(
            workloadId, channelId, installationId, guestDigest, null, "1.0", bootToken, expiresAt,
            new HashSet<string>(StringComparer.Ordinal) { "build.fetch", "build.artifact", "build.progress" },
            10_000, 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024);
        var boot = new GuestBootConfiguration
        {
            WorkloadId = workloadId.ToString("D"),
            ChannelId = channelId.ToString("D"),
            ProtocolVersion = "1.0",
            GuestImageDigest = guestDigest,
            BootToken = bootToken,
            LeaseExpiresAtUnixSeconds = expiresAt.ToUnixTimeSeconds(),
            ArtifactRoot = "/usr/lib/csweet/builder",
            WorkloadKind = (int)IsolationWorkloadKind.Builder,
            LocalBrokerSocketPath = "/run/csweet/broker.sock",
            WorkloadTokenPath = "/run/csweet/workload-token",
            MaximumFrameBytes = 16 * 1024 * 1024
        };
        var start = new StartCommand
        {
            WorkloadKind = (int)IsolationWorkloadKind.Builder,
            MaximumLogBytes = 4 * 1024 * 1024
        };
        start.Entrypoint.AddRange([
            "/usr/lib/csweet/builder/CSweet.AgentRuntime.Builder",
            "--repository", workload.Repository.RepositoryUrl,
            "--commit", workload.Repository.CommitSha,
            "--project", "src/SmokeAgent/SmokeAgent.csproj",
            "--maximum-repository-bytes", (10 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--maximum-artifact-bytes", workload.MaximumArtifactBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--broker-socket", "/run/csweet/broker.sock",
            "--target-framework", "net10.0"
        ]);
        var hostSession = new GuestBrokerHostSession(
            grant,
            handler,
            TimeProvider.System,
            bootConfiguration: boot,
            startCommand: start);
        await using var transport = await ConnectGuestAsync(provider, helper, handle, socketOptions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var sessionTask = hostSession.RunAsync(transport, transport, timeout.Token);
        var startupCompleted = await Task.WhenAny(hostSession.Started, sessionTask).WaitAsync(timeout.Token);
        if (startupCompleted == sessionTask) await sessionTask;
        await hostSession.Started.WaitAsync(timeout.Token);
        await sessionTask.WaitAsync(timeout.Token);
        var artifact = await handler.Artifact.WaitAsync(timeout.Token);
        var artifactChecks = ValidateBuilderArtifact(artifact);
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["builder-source-brokered"] = handler.SourceFetched,
            ["builder-package-trust-metadata-brokered"] = handler.SignatureMetadataFetched,
            ["builder-progress-reported"] = handler.ProgressSucceeded,
            ["builder-artifact-streamed"] = artifact.Length > 0,
            ["builder-artifact-digest-verified"] = handler.ArtifactDigestVerified,
            ["builder-artifact-entrypoint-executable"] = artifactChecks
        };
    }
    finally
    {
        if (handle is not null)
        {
            try { EnsureSuccess(await InvokeHelperAsync(helper, "stop", new PlatformHelperRequest { Handle = handle, GracePeriodSeconds = 5 })); }
            catch { }
            EnsureSuccess(await InvokeHelperAsync(helper, "destroy", new PlatformHelperRequest { Handle = handle }));
        }
    }
}

static byte[] CreateBuilderFixtureArchive()
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        Write("certification-fixture/csweet-plugin.json", "{}");
        Write("certification-fixture/src/SmokeAgent/SmokeAgent.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        Write("certification-fixture/src/SmokeAgent/Program.cs", "Console.WriteLine(\"C-Sweet builder certification\");");
        void Write(string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
    return output.ToArray();
}

static bool ValidateBuilderArtifact(byte[] artifact)
{
    using var input = new MemoryStream(artifact, writable: false);
    using var reader = new TarReader(input);
    var foundManifest = false;
    var foundExecutable = false;
    while (reader.GetNextEntry(copyData: false) is { } entry)
    {
        if (entry.Name == "artifact.json") foundManifest = true;
        if (entry.Name == "payload/SmokeAgent" && (entry.Mode & UnixFileMode.UserExecute) != 0)
            foundExecutable = true;
    }
    return foundManifest && foundExecutable;
}

static async Task<Stream> ConnectGuestAsync(
    string provider,
    string helper,
    IsolationWorkloadHandle handle,
    HyperVSocketTransportOptions socketOptions)
{
    if (provider == "hyperv")
    {
        if (!Guid.TryParseExact(handle.ProviderInstanceId, "N", out var virtualMachineId))
            throw new InvalidDataException("The helper returned an invalid virtual machine identifier.");
        return await new WindowsHyperVSocketTransport(socketOptions).ConnectAsync(virtualMachineId);
    }

    var options = new FirecrackerIsolationBackendOptions
    {
        HelperExecutablePath = helper,
        HelperExecutableDigest = await DigestAsync(helper),
        RequiredGuestChannelTransport = ExternalPlatformStdioGuestChannelConnector.TransportName,
        GuestChannelConnectTimeoutSeconds = 120
    };
    return await new FirecrackerGuestChannelConnector(options).OpenGuestChannelAsync(handle);
}

static void RegisterHyperVSocket(Guid serviceId)
{
    Registry.LocalMachine.DeleteSubKeyTree(
        WindowsHyperVSocketServiceRegistration.LegacyBracedServiceKeyPath(serviceId),
        throwOnMissingSubKey: false);
    using var key = Registry.LocalMachine.CreateSubKey(
        WindowsHyperVSocketServiceRegistration.ServiceKeyPath(serviceId),
        writable: true) ?? throw new InvalidOperationException("The Hyper-V socket registry key could not be created.");
    key.SetValue("ElementName", "C-Sweet Windows isolation certification", RegistryValueKind.String);
}

static async Task<string> CreateProbeBundleAsync(string probePath, string outputPath, string architecture)
{
    await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
    await using (var probe = new FileStream(probePath, FileMode.Open, FileAccess.Read, FileShare.Read))
    using (var writer = new TarWriter(output, leaveOpen: true))
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = "1.0",
            operatingSystem = "linux",
            architecture,
            entrypoint = new[] { "probe" }
        });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "artifact.json")
        {
            DataStream = new MemoryStream(manifest), Uid = 0, Gid = 0,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/probe")
        {
            DataStream = probe, Uid = 0, Gid = 0,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        });
    }
    return await DigestAsync(outputPath);
}

static async Task<string> DigestAsync(string path)
{
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    return "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
}

static async Task<PlatformHelperResponse> InvokeHelperAsync(
    string helper,
    string operation,
    PlatformHelperRequest request)
{
    var start = new ProcessStartInfo
    {
        FileName = helper,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("--protocol");
    start.ArgumentList.Add("1.0");
    start.ArgumentList.Add("--operation");
    start.ArgumentList.Add(operation);
    using var process = Process.Start(start) ?? throw new IOException("The Hyper-V helper could not be started.");
    await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, SmokeJson.Options));
    process.StandardInput.Close();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
    var error = process.StandardError.ReadToEndAsync(timeout.Token);
    await process.WaitForExitAsync(timeout.Token);
    if (process.ExitCode != 0) throw new IOException("The platform helper failed: " + await error);
    return JsonSerializer.Deserialize<PlatformHelperResponse>(await output, SmokeJson.Options) ??
        throw new InvalidDataException("The platform helper returned an empty response.");
}

static void EnsureSuccess(PlatformHelperResponse response)
{
    if (!response.Success)
        throw new InvalidOperationException($"Platform helper rejected the smoke test ({response.ErrorCode}): {response.SanitizedError}");
}

internal sealed class CertificationReportHandler : IAgentBrokerOperationHandler
{
    private readonly TaskCompletionSource<SmokeGuestReport> _report =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<SmokeGuestReport> Report => _report.Task;

    public Task<BrokerOperationResult> HandleAsync(
        BrokerOperationContext request,
        CancellationToken cancellationToken)
    {
        if (request.Purpose != "mcp.runtime" || request.Method != "POST" || request.Path != "/mcp")
            throw new UnauthorizedAccessException();
        var report = JsonSerializer.Deserialize<SmokeGuestReport>(request.Body.Span, SmokeJson.Options) ??
            throw new InvalidDataException("The guest certification report is empty.");
        _report.TrySetResult(report);
        return Task.FromResult(new BrokerOperationResult(
            200, new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Encoding.UTF8.GetBytes("{}")));
    }
}

internal sealed class BuilderCertificationHandler(byte[] sourceArchive) : IAgentBrokerOperationHandler
{
    private readonly MemoryStream _artifact = new();
    private readonly IncrementalHash _artifactHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly TaskCompletionSource<byte[]> _artifactCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextSequence;

    public Task<byte[]> Artifact => _artifactCompletion.Task;
    public bool SourceFetched { get; private set; }
    public bool SignatureMetadataFetched { get; private set; }
    public bool ProgressSucceeded { get; private set; }
    public bool ArtifactDigestVerified { get; private set; }

    public Task<BrokerOperationResult> HandleAsync(
        BrokerOperationContext request,
        CancellationToken cancellationToken) => request.Purpose switch
        {
            "build.fetch" => Task.FromResult(Fetch(request)),
            "build.artifact" => ReceiveArtifactAsync(request, cancellationToken),
            "build.progress" => Task.FromResult(Progress(request)),
            _ => throw new UnauthorizedAccessException()
        };

    private BrokerOperationResult Fetch(BrokerOperationContext request)
    {
        var fetch = JsonSerializer.Deserialize<BuilderFetch>(request.Body.Span, SmokeJson.Options)
            ?? throw new InvalidDataException("The builder certification fetch was empty.");
        byte[] content;
        string contentType;
        if (fetch.Url.StartsWith("https://codeload.github.com/", StringComparison.Ordinal))
        {
            content = sourceArchive;
            contentType = "application/zip";
            SourceFetched = true;
        }
        else if (fetch.Url == "https://api.nuget.org/v3/index.json")
        {
            content = Encoding.UTF8.GetBytes("{\"version\":\"3.0.0\",\"resources\":[]}");
            contentType = "application/json";
        }
        else if (fetch.Url == "https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json")
        {
            content = Encoding.UTF8.GetBytes("""
                {
                  "allRepositorySigned": true,
                  "signingCertificates": [
                    {
                      "fingerprints": {
                        "2.16.840.1.101.3.4.2.1": "1f4b311d9acc115c8dc8018b5a49e00fce6da8e2855f9f014ca6f34570bc482d"
                      }
                    }
                  ]
                }
                """);
            contentType = "application/json";
            SignatureMetadataFetched = true;
        }
        else throw new UnauthorizedAccessException("The builder certification requested an unexpected source.");
        if (fetch.Offset < 0 || fetch.MaximumBytes is < 1 or > 1024 * 1024)
            throw new InvalidDataException("The builder certification fetch range was invalid.");
        var remaining = Math.Max(0, content.Length - checked((int)Math.Min(fetch.Offset, int.MaxValue)));
        var count = Math.Min(remaining, fetch.MaximumBytes);
        var body = count == 0
            ? ReadOnlyMemory<byte>.Empty
            : content.AsMemory(checked((int)fetch.Offset), count);
        var complete = fetch.Offset + count >= content.Length;
        return new BrokerOperationResult(
            200,
            new Dictionary<string, string>
            {
                ["X-CSweet-Complete"] = complete ? "true" : "false",
                ["X-CSweet-Content-Type"] = contentType
            },
            body);
    }

    private async Task<BrokerOperationResult> ReceiveArtifactAsync(
        BrokerOperationContext request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue("X-CSweet-Sequence", out var sequenceValue) ||
            !long.TryParse(sequenceValue, out var sequence) || sequence != _nextSequence ||
            !request.Headers.TryGetValue("X-CSweet-Completed", out var completedValue) ||
            !bool.TryParse(completedValue, out var completed))
            throw new InvalidDataException("The builder certification artifact sequence was invalid.");
        if (!request.Body.IsEmpty)
        {
            _artifactHash.AppendData(request.Body.Span);
            await _artifact.WriteAsync(request.Body, cancellationToken);
        }
        _nextSequence++;
        if (completed)
        {
            if (!request.Headers.TryGetValue("X-CSweet-Digest", out var expected))
                throw new InvalidDataException("The builder certification artifact digest was missing.");
            var actual = "sha256:" + Convert.ToHexStringLower(_artifactHash.GetHashAndReset());
            ArtifactDigestVerified = string.Equals(expected, actual, StringComparison.Ordinal);
            if (!ArtifactDigestVerified)
                throw new InvalidDataException("The builder certification artifact digest did not match.");
            _artifactCompletion.TrySetResult(_artifact.ToArray());
        }
        return new BrokerOperationResult(200, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty);
    }

    private BrokerOperationResult Progress(BrokerOperationContext request)
    {
        var progress = JsonSerializer.Deserialize<BuilderProgress>(request.Body.Span, SmokeJson.Options)
            ?? throw new InvalidDataException("The builder certification progress was empty.");
        if (progress.Status == "failed")
            throw new InvalidOperationException("The builder certification reported failure: " + progress.Detail);
        if (progress.Step == "package" && progress.Status == "succeeded")
            ProgressSucceeded = true;
        return new BrokerOperationResult(200, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty);
    }

    private sealed record BuilderFetch(string Url, long Offset, int MaximumBytes);
    private sealed record BuilderProgress(string Step, string Status, string Detail);
}

internal sealed record SmokeGuestReport(
    string Suite,
    bool Passed,
    IReadOnlyDictionary<string, bool> Checks,
    string GuestOperatingSystem,
    DateTimeOffset CompletedAt);

internal static class SmokeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed record SmokeArguments(
    string Provider,
    string HelperExecutablePath,
    string GuestImagePath,
    string ProbeExecutablePath,
    string OutputRoot,
    string EvidenceOutputPath,
    bool PreserveFailedVirtualMachine)
{
    public static SmokeArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Smoke-test arguments must be --name value pairs.");
            values[args[index][2..]] = args[index + 1];
        }
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Required argument --{name} is missing.");
        var preserve = values.TryGetValue("preserve-failed-vm", out var preserveValue) &&
            bool.TryParse(preserveValue, out var parsedPreserve) && parsedPreserve;
        var provider = values.TryGetValue("provider", out var providerValue)
            ? providerValue.Trim().ToLowerInvariant()
            : "hyperv";
        if (provider is not ("hyperv" or "firecracker"))
            throw new ArgumentException("--provider must be hyperv or firecracker.");
        return new SmokeArguments(
            provider,
            Required("helper"), Required("guest-image"), Required("probe"),
            Required("output-root"), Required("evidence"), preserve);
    }

    public string ValidatedOutputRoot()
    {
        if (!Path.IsPathFullyQualified(OutputRoot)) throw new ArgumentException("The smoke-test output root must be absolute.");
        var full = Path.GetFullPath(OutputRoot);
        if (Path.GetPathRoot(full) == full) throw new ArgumentException("The smoke-test output root cannot be a filesystem root.");
        Directory.CreateDirectory(full);
        return full;
    }

    public string RequireFile(string value, string extension)
    {
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Smoke-test file paths must be absolute.");
        var full = Path.GetFullPath(value);
        if (!File.Exists(full) || extension.Length > 0 && !Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("A required smoke-test file is missing or has the wrong type.", full);
        return full;
    }
}
