using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Compute.HyperV;
using CSweet.Compute.Runtime;
using CSweet.Domain.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeHyperVExecutorTests
{
    internal sealed class Runner : IHyperVCommandRunner
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Exists { get; private set; }
        public bool LoseCreateResponse { get; set; }
        public bool LoseStartResponseAfterEffect { get; set; }
        public Action? AfterCreate { get; set; }
        public bool LoseControlResponse { get; set; }
        public bool FailObservation { get; set; }
        public string PhysicalState { get; set; } = "Off";
        public List<string> Actions { get; } = [];
        public List<string> Scripts { get; } = [];
        public Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken token)
        {
            Scripts.Add(script);
            if (script == HyperVComputeDriver.CreateScript)
            {
                Exists = true;
                File.WriteAllText(parameters["CSWEET_COMPUTE_DISK"], "simulated private disk");
                AfterCreate?.Invoke();
                if (LoseCreateResponse) throw new IOException("Response lost after VM allocation.");
            }
            if (FailObservation && script == HyperVComputeDriver.ObserveScript) throw new IOException("Inventory unavailable.");
            var state = Exists ? PhysicalState : "Missing";
            if (parameters.TryGetValue("CSWEET_COMPUTE_ACTION", out var action))
            {
                Actions.Add(action);
                if (LoseControlResponse) throw new IOException("Control response lost.");
                if (action == InfrastructureActions.Start)
                {
                    state = PhysicalState = "Running";
                    if (LoseStartResponseAfterEffect) throw new IOException("Response lost after activation.");
                }
                if (action == InfrastructureActions.Stop) state = PhysicalState = "Off";
                if (action == InfrastructureActions.Destroy) { Exists = false; state = "Missing"; }
            }
            Guid? id = Exists || parameters.ContainsKey("CSWEET_COMPUTE_VM_ID") ? Id : null;
            return Task.FromResult(JsonSerializer.Serialize(new { id, state }));
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public ComputeReplayJournalTests.Fixture Journal { get; } = new();
        public Runner Runner { get; } = new();
        public ComputeDispatchPacket Packet { get; private set; } = null!;
        public ComputeDispatchAuthorization Claim { get; private set; } = null!;
        public ComputeDispatchVerifier Verifier { get; private set; } = null!;
        private readonly ECDsa releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private SignedComputeImageCertification certification = null!;
        internal SignedComputeImageCertification Certification => certification;
        internal string ReleasePublicKey => Convert.ToBase64String(releaseKey.ExportSubjectPublicKeyInfo());
        public string Workloads => Path.Combine(Journal.Root, "workloads");
        public string Payloads => Path.Combine(Journal.Root, "payloads");
        public string Image => Path.Combine(Payloads, "clean.vhdx");
        public int PayloadOpens { get; private set; }
        public async Task InitializeAsync(bool persistent = false)
        {
            await Journal.InitializeAsync();
            Directory.CreateDirectory(Workloads); Directory.CreateDirectory(Payloads);
            await File.WriteAllTextAsync(Image, "certified test image");
            var runtime = Path.Combine(Payloads, "runtime.dll");
            await File.WriteAllTextAsync(runtime, "certified test runtime");
            static string Hash(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var template = Journal.Packet.Template! with { ImageDigest = Hash(Image) };
            var specification = Journal.Packet.Specification with { Persistence = persistent ? ComputePersistence.Persistent : ComputePersistence.Ephemeral };
            Claim = Journal.Core.Signing.Claims[0] with
            { TemplateDigest = ComputeProtocol.Digest(JsonSerializer.Serialize(template, ComputeProtocol.Json)),
                SpecificationDigest = ComputeProtocol.Digest(JsonSerializer.Serialize(specification, ComputeProtocol.Json)) };
            if (persistent) Claim = Claim with { Grants = [.. Claim.Grants, new(Guid.NewGuid(), 1, InfrastructureActions.Persist, Claim.ExpiresAt)] };
            Packet = Journal.Packet with { Specification = specification, Template = template, Authorization = await Journal.Core.Signing.SignAsync(Claim, default) };
            Verifier = new(Journal.Enrollment, new(Claim.ProviderId, [template.Id],
                [InfrastructureActions.Provision, InfrastructureActions.Start, InfrastructureActions.Stop, InfrastructureActions.Restart, InfrastructureActions.Destroy, InfrastructureActions.Execute, InfrastructureActions.PublishPort],
                [ComputeNetworkMode.None], false, persistent), new(4, 8192, 40960),
                new Dictionary<string, ComputeTemplate> { [template.Id] = template }, Journal.Core.Time);
            var now = Journal.Core.Time.GetUtcNow();
            var evidence = new ComputeImageCertification(ComputeImageCertificationVerifier.Purpose, Claim.ProviderId, "test-version",
                template, "1", ComputeImageCertificationVerifier.RequiredControls.ToHashSet(),
                new() { ["runtime.dll"] = Hash(runtime) }, now.AddMinutes(-1), now.AddHours(1));
            var json = JsonSerializer.Serialize(evidence, ComputeProtocol.Json);
            certification = new(json, Convert.ToBase64String(releaseKey.SignData(
                ComputeImageCertificationVerifier.Payload(json), HashAlgorithmName.SHA256)));
        }
        private void Guard(string path)
        {
            var resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(Journal.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Test path escaped its workspace or is redirected.");
        }
        public HyperVComputeExecutor Executor(ComputeTemplateCatalog? catalog = null, Func<Guid, CancellationToken, Task<Stream>>? connectGuest = null) => new(Verifier, Journal.Journal(), new(Runner), Workloads,
            async (dispatch, token) =>
            {
                PayloadOpens++;
                if (catalog is not null) return await catalog.OpenAsync(dispatch, token);
                return await ComputeCertifiedPayload.OpenAsync(certification, Convert.ToBase64String(releaseKey.ExportSubjectPublicKeyInfo()),
                    Claim.ProviderId, "test-version", dispatch.Template!, Journal.Core.Time.GetUtcNow(), Image, Payloads,
                    new HashSet<string>(StringComparer.Ordinal) { "runtime.dll" }, Guard, token);
            }, Guard, connectGuest);
        public async Task<ComputeDispatchPacket> Control(string action, long generation)
        {
            var claim = Claim with { Action = action, Generation = generation, OperationId = Guid.NewGuid(), DispatchId = Guid.NewGuid(),
                ResourceId = Runner.Id.ToString("D"), TemplateDigest = null, Grants = [new(Guid.NewGuid(), 1, action, Claim.ExpiresAt)] };
            if (Packet.Specification.Persistence == ComputePersistence.Persistent && action is InfrastructureActions.Start or InfrastructureActions.Restart)
                claim = claim with { Grants = [.. claim.Grants, new(Guid.NewGuid(), 1, InfrastructureActions.Persist, claim.ExpiresAt)] };
            return Packet with { Authorization = await Journal.Core.Signing.SignAsync(claim, default), Template = null };
        }
        public async ValueTask DisposeAsync() { releaseKey.Dispose(); await Journal.DisposeAsync(); }
    }

    [Fact]
    public async Task Signed_provision_replay_start_and_destroy_remove_only_the_owned_vm_and_disk()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        Assert.Equal("Running", (await f.Executor().ExecuteAsync(f.Packet, default)).State);
        Assert.Equal(f.Runner.Id, (await f.Executor().ExecuteAsync(f.Packet, default)).Id);
        Assert.Equal(1, f.Runner.Scripts.Count(x => x == HyperVComputeDriver.CreateScript));
        Assert.Contains(HyperVComputeDriver.ObserveScript, f.Runner.Scripts);
        Assert.Equal(1, f.PayloadOpens);
        Assert.Equal(InfrastructureActions.Start, Assert.Single(f.Runner.Actions));
        Assert.Equal("Running", (await f.Executor().ExecuteAsync(await f.Control(InfrastructureActions.Start, 2), default)).State);
        Assert.Equal("Missing", (await f.Executor().ExecuteAsync(await f.Control(InfrastructureActions.Destroy, 3), default)).State);
        var reservation = Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
        Assert.True(reservation.DestroyRequested);
        Assert.Equal(f.Runner.Id.ToString("D"), reservation.ResourceId);
        Assert.False(File.Exists(Path.Combine(f.Workloads, f.Claim.EnvironmentId.ToString("N"), "os.vhdx")));
        Assert.Contains(await f.Journal.Journal().ListResultsAsync(100, default), x => x.Result.State == ComputeLifecycleState.Destroyed && x.Result.TeardownConfirmed);
    }

    [Fact]
    public async Task Observation_only_destroy_does_not_delete_unconfirmed_workload_files()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await f.Executor().ExecuteAsync(f.Packet, default);
        var packet = await f.Control(InfrastructureActions.Destroy, 2);
        var claim = JsonSerializer.Deserialize<ComputeDispatchAuthorization>(packet.Authorization.PayloadJson, ComputeProtocol.Json)!;
        f.Runner.LoseControlResponse = true;
        await Assert.ThrowsAsync<IOException>(() => f.Executor().ExecuteAsync(packet, default));
        f.Runner.LoseControlResponse = false;
        await new HyperVComputeDriver(f.Runner).ApplyAsync(new(claim.NodeId, claim.OrganizationId, claim.InstallationId, claim.EnvironmentId), f.Runner.Id, InfrastructureActions.Destroy, default);
        var observation = packet with { Authorization = await f.Journal.Core.Signing.SignAsync(claim with { Mode = ComputeDispatchMode.Observe, Grants = [] }, default) };
        await f.Executor().ExecuteAsync(observation, default);
        Assert.True(File.Exists(Path.Combine(f.Workloads, claim.EnvironmentId.ToString("N"), "os.vhdx")));
        Assert.DoesNotContain(await f.Journal.Journal().ListResultsAsync(100, default), x => x.Result.OperationId == claim.OperationId && x.Result.TeardownConfirmed);
    }

    [Fact]
    public async Task Lost_creation_response_discovers_owned_vm_without_repeating_provision()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); f.Runner.LoseCreateResponse = true;
        await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.Equal(f.Runner.Id.ToString("D"), Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
        var recovered = await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.Equal(f.Runner.Id, recovered.Id);
        Assert.Equal("Off", recovered.State); Assert.Empty(f.Runner.Actions);
        Assert.Equal(1, f.Runner.Scripts.Count(x => x == HyperVComputeDriver.CreateScript));
        Assert.Contains(HyperVComputeDriver.DiscoverScript, f.Runner.Scripts);
        Assert.Equal(f.Runner.Id.ToString("D"), Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default)).ResourceId);
    }

    [Fact]
    public async Task Changed_image_fails_before_host_commands_and_missing_vm_does_not_release_capacity()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        await File.AppendAllTextAsync(f.Image, "tampering");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Executor().ExecuteAsync(f.Packet, default));
        Assert.Empty(f.Runner.Scripts);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Workloads));
        var observed = await f.Executor().ExecuteAsync(f.Packet, default);
        Assert.Equal("Missing", observed.State); Assert.Null(observed.Id);
        Assert.Single(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }

    [Fact]
    public async Task Invalid_dispatch_signature_never_reaches_journal_payload_or_driver()
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var packet = f.Packet with { Authorization = f.Packet.Authorization with { SignatureBase64 = "invalid" } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Executor().ExecuteAsync(packet, default));
        Assert.Empty(f.Runner.Scripts); Assert.Equal(0, f.PayloadOpens);
        Assert.Empty(await f.Journal.Journal().ListReservationsAsync(null, 100, default));
    }
}
