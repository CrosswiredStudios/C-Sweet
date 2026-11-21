using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;
using CSweet.Isolation.HyperV;

// One-shot hardware experiment. The receiver has no listener and accepts only its own test packets.
// The production release pipeline supplies image certification separately.
if (!OperatingSystem.IsWindows()) return 2;
// Independent witness for an environment already created by the installed Office.
// The acceptance harness resolves the exact Hyper-V ID from the HQ environment ID.
if (args is ["check-office-guest", var physical] && Guid.TryParseExact(physical, "D", out var physicalId) && physicalId != Guid.Empty)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var socket = new WindowsHyperVSocketTransport(new() { LinuxVsockPort = ComputeGuestWire.Port });
    Task<Stream> Connect(CancellationToken ct) => socket.ConnectAsync(physicalId, ct);
    if (!await ComputeGuestReadiness.ProbeAsync(Connect, "linux", "x64", deadline.Token)) return 1;
    var marker = Guid.NewGuid().ToString("N");
    var command = new ComputeGuestCommand(Guid.NewGuid(), "/usr/bin/python3", "/tmp",
        ["-c", "import sys,tempfile; f=tempfile.TemporaryFile(); f.write(sys.argv[1].encode()); f.seek(0); print(f.read().decode())", marker],
        10, 1024);
    var receipt = await ComputeGuestCommandClient.ExecuteAsync(Connect, command, "linux", deadline.Token);
    if (receipt.ExitCode != 0 || Encoding.UTF8.GetString(receipt.StandardOutput).TrimEnd('\r', '\n') != marker) return 1;
    Console.WriteLine($"Linux guest {physicalId:D} executed the file round trip.");
    return 0;
}
if (args is ["observe", var retainedId] && Guid.TryParseExact(retainedId, "N", out var retained))
{
    var path = ProbeRoot(retained);
    WindowsComputeProtectedPaths.Verify(path);
    var state = JsonSerializer.Deserialize<ReplayState>(File.ReadAllText(Path.Combine(path, "redelivery.json")), ComputeProtocol.Json)!;
    var verified = Verifier(state.Enrollment, state.Template).Verify(state.Packet);
    if (verified.Authorization.EnvironmentId != retained) throw new InvalidDataException("Unexpected environment.");
    var result = await Journal(path, state.Enrollment).RunPhysicalAsync(verified,
        async (decision, resource, token) =>
        {
            if (decision != ComputeJournalDecision.Observe) throw new InvalidOperationException("Duplicate execution.");
            if (verified.Authorization.Action == InfrastructureActions.Provision && resource is null)
            {
                var observed = await new HyperVCommandRunner().RunAsync(ProbeVm.Observe, VmParameters(retained), token);
                return new ComputePhysicalOutcome<bool>(true, Guid.Parse(observed).ToString("D"), new(ComputeLifecycleState.Bootstrapping));
            }
            if (verified.Authorization.Action != InfrastructureActions.Execute || resource is null || resource != verified.Authorization.ResourceId)
                throw new InvalidOperationException("Unexpected action or guest identity on redelivery.");
            return new ComputePhysicalOutcome<bool>(true, resource, new(ComputeLifecycleState.Ready, Workload: new(ErrorCode: "outcome-unknown")));
        }, default);
    return result ? 0 : 1;
}
if (args is not ["run", var image, var digest] || digest.Length != 64 || !digest.All(Uri.IsHexDigit)) return 2;
var runId = Guid.NewGuid();
var root = ProbeRoot(runId);
var report = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $"CSweet-Transport-{runId:N}.txt");
using var log = new StreamWriter(new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
var runner = new HyperVCommandRunner();
var parameters = VmParameters(runId);
parameters["CSWEET_PROBE_IMAGE"] = Path.GetFullPath(image);
var success = false;
var ownsRoot = false;
try
{
    WindowsComputeProtectedPaths.Verify(parameters["CSWEET_PROBE_IMAGE"]);
    await using var source = File.OpenRead(image);
    if (!Convert.ToHexString(await SHA256.HashDataAsync(source)).Equals(digest, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The input image digest differs from the operator-selected image.");
    source.Close();
    if (Path.Exists(root)) throw new IOException("Test directory already exists.");
    Directory.CreateDirectory(root);
    ownsRoot = true;
    WindowsComputeProtectedPaths.Verify(root);
    Directory.CreateDirectory(Path.Combine(root, "journal"));
    Directory.CreateDirectory(Path.Combine(root, "vm"));
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var enrollment = new ComputeProviderEnrollment(Guid.NewGuid(), Guid.NewGuid(), "hyperv-spike",
        new("spike", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    var template = new ComputeTemplate("linux-probe", "linux", "x64", "sha256:" + digest.ToLowerInvariant(), []);
    var spec = new ComputeSpecification("linux", "x64", template.Id, new(1, 2048, 65536), 900);
    var installation = Guid.NewGuid();
    var lease = DateTimeOffset.UtcNow.AddMinutes(10);
    var verifier = Verifier(enrollment, template);
    var journal = Journal(root, enrollment);
    await journal.InitializeAsync(default);
    ComputeDispatchPacket Issue(long generation, string? resource = null, ComputeGuestCommand? command = null)
    {
        var now = DateTimeOffset.UtcNow;
        var action = command is null ? InfrastructureActions.Provision : InfrastructureActions.Execute;
        var workload = command is null ? null : new ComputeWorkload(command);
        var claim = new ComputeDispatchAuthorization(Guid.NewGuid(), Guid.NewGuid(), runId, enrollment.OrganizationId,
            installation, enrollment.NodeId, enrollment.ProviderId, generation, action, ComputeDispatchMode.Execute,
            Digest(spec), command is null ? Digest(template) : null, resource, now, now.AddMinutes(1), lease,
            [new(Guid.NewGuid(), 1, action, lease)], workload is null ? null : Digest(workload));
        var json = JsonSerializer.Serialize(claim, ComputeProtocol.Json);
        return new(new("spike", json, Convert.ToBase64String(key.SignData(ComputeProtocol.DispatchPayload(json), HashAlgorithmName.SHA256))),
            spec, command is null ? template : null, workload);
    }
    var create = Issue(1);
    try
    {
        await journal.RunPhysicalAsync<bool>(verifier.Verify(create), async (decision, _, token) =>
        {
            if (decision != ComputeJournalDecision.Execute) throw new InvalidOperationException("First provision did not acquire its reservation.");
            await runner.RunAsync(ProbeVm.Create, parameters, token);
            throw new LostAcknowledgment();
        }, default);
    }
    catch (LostAcknowledgment) { log.WriteLine("Dropped the create response before recording the VM identity."); }
    await ReplayAsync(runId, new(enrollment, template, create));
    var reservation = (await journal.ListReservationsAsync(null, 2, default)).Single();
    var vm = Guid.Parse(reservation.ResourceId ?? throw new InvalidOperationException("The restarted receiver could not recover the owned VM."));
    log.WriteLine($"Recovered {vm:D} from sha256:{digest} with zero network adapters.");
    var transport = new WindowsHyperVSocketTransport(new() { LinuxVsockPort = ComputeGuestWire.Port, ConnectTimeoutSeconds = 120 });
    using var readyDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    while (!await ComputeGuestReadiness.ProbeAsync(ct => transport.ConnectAsync(vm, ct), "linux", "x64", readyDeadline.Token))
        await Task.Delay(1000, readyDeadline.Token);

    const string marker = "/tmp/csweet-transport-probe";
    var append = Issue(2, vm.ToString("D"), new(Guid.NewGuid(), "/usr/bin/python3", "/tmp",
        ["-c", "import sys; open(sys.argv[1], 'a').write(sys.argv[2]+'\\n')", marker, runId.ToString("N")], 10, 1024));
    var verified = verifier.Verify(append);
    try
    {
        await journal.RunPhysicalAsync<bool>(verified, async (decision, resource, token) =>
        {
            if (decision != ComputeJournalDecision.Execute || resource != vm.ToString("D")) throw new InvalidOperationException("Invalid command admission.");
            var result = await ComputeGuestCommandClient.ExecuteAsync(ct => transport.ConnectAsync(vm, ct), verified.Workload!.Command!, "linux", token);
            if (result.ExitCode != 0) throw new IOException($"Guest marker failed: {result.ErrorCode}, exit {result.ExitCode}.");
            throw new LostAcknowledgment();
        }, default);
    }
    catch (LostAcknowledgment) { log.WriteLine("Dropped the guest response before committing the result."); }
    await ReplayAsync(runId, new(enrollment, template, append));
    var evidence = await journal.ListResultsAsync(100, default);
    if (evidence.Single(x => x.Result.OperationId == verified.Authorization.OperationId).Result.Workload?.ErrorCode != "outcome-unknown")
        throw new InvalidOperationException("Missing unknown-outcome receipt.");
    var read = verifier.Verify(Issue(3, vm.ToString("D"), new(Guid.NewGuid(), "/usr/bin/cat", "/tmp", [marker], 10, 1024)));
    var contents = await journal.RunPhysicalAsync(read, async (decision, resource, token) =>
    {
        if (decision != ComputeJournalDecision.Execute || resource != vm.ToString("D")) throw new InvalidOperationException("Invalid read admission.");
        var result = await ComputeGuestCommandClient.ExecuteAsync(ct => transport.ConnectAsync(vm, ct), read.Workload!.Command!, "linux", token);
        if (result.ExitCode != 0) throw new IOException("Guest marker read failed.");
        return new ComputePhysicalOutcome<string>(Encoding.UTF8.GetString(result.StandardOutput), resource);
    }, default);
    if (contents != runId.ToString("N") + "\n" || (await journal.ListReservationsAsync(null, 2, default)).Single().ResourceId != vm.ToString("D"))
        throw new InvalidDataException("Guest marker or reservation count is incorrect.");
    log.WriteLine("One provision, one retained reservation, one guest marker. Fresh receiver reported outcome-unknown.");
    success = true;
}
catch (Exception ex) { log.WriteLine(ex); }
finally
{
    if (ownsRoot)
    {
        await runner.RunAsync(ProbeVm.Remove, parameters, default);
        // ProbeRoot always produces a fresh GUID child of Program Files.
        if (root != ProbeRoot(runId) || new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Unexpected test directory during cleanup.");
        Directory.Delete(root, true);
        log.WriteLine("Cleaned up the test VM, differencing disk and private journal.");
    }
}
return success ? 0 : 1;

static string ProbeRoot(Guid id) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), $"CSweet-TransportProbe-{id:N}");
static Dictionary<string, string> VmParameters(Guid id) => new()
{
    ["CSWEET_PROBE_NAME"] = $"CSweet-Transport-{id:N}",
    ["CSWEET_PROBE_DISK"] = Path.Combine(ProbeRoot(id), "vm", "test.vhdx")
};
static async Task ReplayAsync(Guid id, ReplayState state)
{
    File.WriteAllText(Path.Combine(ProbeRoot(id), "redelivery.json"), JsonSerializer.Serialize(state, ComputeProtocol.Json));
    var child = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    child.ArgumentList.Add("observe");
    child.ArgumentList.Add(id.ToString("N"));
    using var observer = Process.Start(child) ?? throw new IOException("Restart failed.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    try { await observer.WaitForExitAsync(deadline.Token); }
    catch (OperationCanceledException) { if (!observer.HasExited) observer.Kill(true); await observer.WaitForExitAsync(); throw; }
    if (observer.ExitCode != 0) throw new InvalidOperationException("Restarted receiver failed.");
}
static string Digest<T>(T value) => ComputeProtocol.Digest(JsonSerializer.Serialize(value, ComputeProtocol.Json));
static ComputeReplayJournal Journal(string root, ComputeProviderEnrollment enrollment) =>
    new(Path.Combine(root, "journal"), enrollment, new(1, new(1, 2048, 65536)), TimeProvider.System);
static ComputeDispatchVerifier Verifier(ComputeProviderEnrollment enrollment, ComputeTemplate template) => new(enrollment,
    new(enrollment.ProviderId, [template.Id], [InfrastructureActions.Provision, InfrastructureActions.Execute], [ComputeNetworkMode.None], false, false),
    new(1, 2048, 65536), new Dictionary<string, ComputeTemplate> { [template.Id] = template }, TimeProvider.System);

record ReplayState(ComputeProviderEnrollment Enrollment, ComputeTemplate Template, ComputeDispatchPacket Packet);
sealed class LostAcknowledgment : Exception;
