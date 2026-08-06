using System.Diagnostics;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Core;
using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.Protocol;
using Microsoft.Win32;

var arguments = SmokeArguments.Parse(args);
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The smoke test requires Windows Hyper-V.");
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Run the Windows isolation smoke test from the administrator prompt opened by the test bootstrap script.");

var root = arguments.ValidatedOutputRoot();
var mediaRoot = Path.Combine(root, "artifact-media");
var helperRoot = Path.Combine(root, "hyperv-helper-data");
Directory.CreateDirectory(mediaRoot);
Directory.CreateDirectory(helperRoot);
Environment.SetEnvironmentVariable("CSWEET_ARTIFACT_MEDIA_ROOT", mediaRoot);
Environment.SetEnvironmentVariable("CSWEET_HYPERV_DATA_ROOT", helperRoot);
var socketOptions = new HyperVSocketTransportOptions { ConnectTimeoutSeconds = 120 };
var serviceId = socketOptions.ServiceId;
Environment.SetEnvironmentVariable("CSWEET_HYPERV_BROKER_SERVICE_ID", serviceId.ToString("D"));
RegisterHyperVSocket(serviceId);

var guestImage = arguments.RequireFile(arguments.GuestImagePath, ".vhdx");
var helper = arguments.RequireFile(arguments.HelperExecutablePath, ".exe");
var probe = arguments.RequireFile(arguments.ProbeExecutablePath, string.Empty);
var guestDigest = await DigestAsync(guestImage);
var bundle = Path.Combine(root, "certification-probe.csab");
var artifactDigest = await CreateProbeBundleAsync(probe, bundle);
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
var image = new GuestImageReference("csweet-hyperv-test", "1.0", guestDigest, "linux", "x64");
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
        IsolationProviderCatalog.HyperV().ProviderId, workloadId,
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
    await using var transport = await new WindowsHyperVSocketTransport(socketOptions)
        .ConnectAsync(virtualMachineId);
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
        throw new InvalidOperationException("The Hyper-V guest reported a failed isolation check: " +
            string.Join(", ", report.Checks.Where(check => !check.Value).Select(check => check.Key)));
    await sessionTask.WaitAsync(timeout.Token);
    certifiedReport = report;
}
finally
{
    if (handle is not null && arguments.PreserveFailedVirtualMachine && certifiedReport is null)
    {
        Console.Error.WriteLine(
            $"Diagnostic preservation requested. Hyper-V VM id: {handle.ProviderInstanceId}; data root: {helperRoot}");
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
var descriptor = IsolationProviderCatalog.HyperV(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
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
    checks = validatedReport.Checks,
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

static async Task<string> CreateProbeBundleAsync(string probePath, string outputPath)
{
    await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
    await using (var probe = new FileStream(probePath, FileMode.Open, FileAccess.Read, FileShare.Read))
    using (var writer = new TarWriter(output, leaveOpen: true))
    {
        var manifest = Encoding.UTF8.GetBytes(
            "{\"formatVersion\":\"1.0\",\"operatingSystem\":\"linux\",\"architecture\":\"x64\",\"entrypoint\":[\"probe\"]}");
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
    if (process.ExitCode != 0) throw new IOException("The Hyper-V helper failed: " + await error);
    return JsonSerializer.Deserialize<PlatformHelperResponse>(await output, SmokeJson.Options) ??
        throw new InvalidDataException("The Hyper-V helper returned an empty response.");
}

static void EnsureSuccess(PlatformHelperResponse response)
{
    if (!response.Success)
        throw new InvalidOperationException($"Hyper-V helper rejected the smoke test ({response.ErrorCode}): {response.SanitizedError}");
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
        return new SmokeArguments(
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
