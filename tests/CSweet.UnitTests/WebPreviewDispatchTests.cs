using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Isolation.Security;
using CSweet.WebHost.Contracts;
using CSweet.WebHost.Core;
using CSweet.WorkManagement.Contracts;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class WebPreviewDispatchTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-11T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IAgentArtifactStore
    {
        public byte[] Bytes { get; set; } = [];
        public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(Bytes));
        public Task<AgentArtifactReference> ImportAsync(Stream content, ArtifactImportDescriptor descriptor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public Clock Clock { get; } = new();
        public Store Store { get; } = new();
        public ECDsa Authority { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa ReleaseKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa Identity { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Agent { get; } = Guid.NewGuid();
        public Guid Provider { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public Guid Repo { get; } = Guid.NewGuid();
        public Guid Build { get; } = Guid.NewGuid();
        public WebHostRegistration Host { get; private set; } = null!;
        public WebPreviewGrantRecord Grant { get; private set; } = null!;
        public WebHostRegistryOptions Registry { get; } = new() { ControlPlaneId = Guid.NewGuid(), AuthorizationVerificationKeyId = "test-authority" };
        public WebHostExecutionOptions Execution { get; } = new();
        private long sequence;
        public WebPreviewExecutionService Service => new(Db, new WebPreviewGrantService(Db, Clock), new WebPreviewArtifactService(Db, Store),
            Catalog, Signer, Options.Create(Registry), Clock);
        public WebHostReleaseCatalog Catalog => new(Options.Create(Execution), Signer, Clock);
        public WebHostAuthorizationSigner Signer => new(Options.Create(Execution), Options.Create(Registry));
        public PreviewRequest Request(string key = "demo") => new(Project, Repo, Provider,
            new(1, PreviewMode.Static, new string('a', 40), null, JsonSerializer.SerializeToElement<object?>(null), null,
                ResourceBudget.Default, 900, []), key, Build);
        public async Task SeedAsync()
        {
            var actor = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = Org, AgentInstallationId = Agent, EmployeeType = EmployeeType.Agent, DisplayName = "Developer" };
            Db.CoreOrganizationUsers.Add(actor);
            Db.Workstreams.Add(new() { Id = Project, OrganizationId = Org, Name = "Preview", AccountableManagerOrganizationUserId = actor.Id });
            Db.SourceControlRepositories.Add(new() { Id = Repo, OrganizationId = Org, Name = "Product" });
            Db.AgentInstallations.Add(new()
            {
                Id = Agent, BusinessId = Org.ToString("D"), PackageVersion = new() { Id = Guid.NewGuid(), AgentId = "test-agent" },
                Grant = new() { Id = Guid.NewGuid(), AgentInstallationId = Agent, RequiredCapabilitiesJson = JsonSerializer.Serialize(WebPreviewCapabilities.All) }
            });
            Db.AgentInstallations.Add(new() { Id = Provider, BusinessId = Org.ToString("D"),
                PackageVersion = new() { Id = Guid.NewGuid(), AgentId = WebPreviewGrantService.PluginId } });
            var id = Guid.NewGuid();
            var policy = new PreviewGrant(id, 2, Org, Project, Agent, Provider, WebPreviewCapabilities.All, [Repo], ResourceBudget.Default,
                2, 100000, 7200, [], Clock.Now.AddDays(1), false);
            Grant = new() { Id = id, OrganizationId = Org, WorkstreamId = Project, InstallationId = Agent, ProviderInstallationId = Provider,
                Revision = 2, Status = "Active", PolicyJson = JsonSerializer.Serialize(policy, PreviewJson.Options), ExpiresAt = policy.ExpiresAt };
            Db.WebPreviewGrants.Add(Grant);
            var bytes = Encoding.UTF8.GetBytes("<html>Private game preview</html>");
            using var archive = new MemoryStream();
            using (var tar = new TarWriter(archive, leaveOpen: true))
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/output/index.html") { DataStream = new MemoryStream(bytes) });
            Store.Bytes = archive.ToArray();
            Db.DeliveryBuilds.Add(new() { Id = Build, OrganizationId = Org, WorkstreamId = Project, RepositoryId = Repo, Status = "Succeeded",
                SourceRevision = new string('a',40), OutputsJson = JsonSerializer.Serialize(new[] { new BuildOutputManifestEntry("index.html",
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "text/html", "web-file") }) });
            Db.ExecutionWorkloadAssignments.Add(new() { Id = Guid.NewGuid(), BusinessId = Org.ToString("D"), DeliveryBuildId = Build,
                WorkloadKind = ExecutionWorkloadKind.ToolchainBuild, Status = ExecutionAssignmentStatus.Completed,
                ResultArtifactDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Store.Bytes)) });
            Registry.AuthorizationVerificationPublicKeyBase64 = Convert.ToBase64String(Authority.ExportSubjectPublicKeyInfo());
            Execution.Enabled = true; Execution.AuthorizationSigningKeyPem = Authority.ExportPkcs8PrivateKeyPem();
            Execution.ReleaseVerificationPublicKeyBase64 = Convert.ToBase64String(ReleaseKey.ExportSubjectPublicKeyInfo());
            var image = "sha256:" + new string('b',64);
            var certificate = new ProductReleaseCertification(ProductReleaseCertificationVerifier.Purpose, "webhost-hyperv-gen2", "0.2.0",
                image, "1", ProductReleaseCertificationVerifier.RequiredControls, new Dictionary<string,string> { ["runtime.dll"] = image },
                Clock.Now.AddDays(-1), Clock.Now.AddDays(1));
            var json = JsonSerializer.Serialize(certificate, PreviewJson.Options);
            Execution.ApprovedReleases.Add(new(json, Convert.ToBase64String(ReleaseKey.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256))));
            var capacity = new ResourceBudget(16, 65536, 1000000, 1024, 32 * 1024 * 1024);
            Host = new() { Id = Guid.NewGuid(), OrganizationId = Org, ProviderInstallationId = Provider, ExpiresAt = Clock.Now.AddDays(1),
                LastHeartbeatAt = Clock.Now, IdentityPublicKeyBase64 = Convert.ToBase64String(Identity.ExportSubjectPublicKeyInfo()),
                MaximumCapacityJson = JsonSerializer.Serialize(capacity, PreviewJson.Options) };
            Host.ReportedHeartbeatJson = JsonSerializer.Serialize(new WebHostHeartbeat(Host.Id, Clock.Now, capacity,
                [new("webhost-hyperv-gen2", "0.2.0", image, true, Clock.Now.AddDays(1), true, null)]), PreviewJson.Options);
            Db.WebHostRegistrations.Add(Host); await Db.SaveChangesAsync();
        }
        public SignedWebHostMessage Message<T>(string action, T body)
        {
            var json = JsonSerializer.Serialize(body, PreviewJson.Options);
            var message = new SignedWebHostMessage(1, Registry.ControlPlaneId, Host.Id, Guid.NewGuid(), ++sequence,
                action, json, WorkloadAuthorizationEnvelope.Digest(json), "", Clock.Now, Clock.Now.AddSeconds(60));
            return message with { SignatureBase64 = Convert.ToBase64String(Identity.SignData(message.Payload(), HashAlgorithmName.SHA256)) };
        }
        public async Task<WebHostCommandDelivery> PollAsync(string action)
        {
            var delivery = (await Service.PollAsync(Message("poll", new WebHostCommandPoll()), default)).Command;
            Assert.NotNull(delivery);
            Assert.Equal(action, (await Db.WebHostCommands.SingleAsync(x => x.Id == delivery.CommandId)).Action);
            return delivery;
        }
        public Task<WebHostCommandResultReceipt> CompleteAsync(WebHostCommandDelivery command, ProductRuntimeResponse result) =>
            Service.CompleteAsync(Message("result", new WebHostCommandResult(command.CommandId, JsonSerializer.Serialize(result, PreviewJson.Options))), default);
        public async Task<(PreviewOperation Job, WebHostCommandDelivery Initialize)> StartToInitializeAsync()
        {
            var job = await Service.StartAsync(Org, Agent, Request(), default);
            var upload = await PollAsync("upload"); await CompleteAsync(upload, new("ArtifactReady"));
            var start = await PollAsync("start");
            var assignment = JsonSerializer.Deserialize<ProductRuntimeRequest>(start.RuntimeRequestJson, PreviewJson.Options)!.Assignment!;
            await CompleteAsync(start, new("Booting", new(assignment.AssignmentId, job.Id, Guid.NewGuid().ToString("N"))));
            return (job, await PollAsync("initialize"));
        }
        public async ValueTask DisposeAsync() { Authority.Dispose(); ReleaseKey.Dispose(); Identity.Dispose(); await Db.DisposeAsync(); }
    }

    [Fact]
    public async Task Verified_build_dispatches_with_real_signatures_and_retains_evidence_after_teardown()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var job = await f.Service.StartAsync(f.Org, f.Agent, f.Request(), default);
        Assert.Equal(PreviewPhase.Starting, job.Phase);
        var upload = await f.PollAsync("upload");
        var assignment = JsonSerializer.Deserialize<ProductRuntimeRequest>(upload.RuntimeRequestJson, PreviewJson.Options)!.Assignment!;
        var verifier = new AssignmentVerifier(new(f.Host.Id, "test", f.Registry.AuthorizationVerificationKeyId,
            f.Registry.AuthorizationVerificationPublicKeyBase64, f.Clock.Now), f.Clock);
        Assert.Equal(f.Build, verifier.Verify(assignment).BuildId);
        using var artifact = await f.Service.ArtifactAsync(f.Message("artifact", new WebHostArtifactRequest(upload.CommandId)), default);
        Assert.Equal(upload.ArtifactDigest, artifact.Digest);
        await f.CompleteAsync(upload, new("ArtifactReady"));
        var start = await f.PollAsync("start");
        await f.CompleteAsync(start, new("Booting", new(assignment.AssignmentId, job.Id, Guid.NewGuid().ToString("N"))));
        var initialize = await f.PollAsync("initialize");
        await f.CompleteAsync(initialize, new("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready)));
        Assert.Equal(PreviewPhase.Ready, (await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default)).Phase);
        var evidence = await f.PollAsync("evidence");
        var diagnostic = new PreviewDiagnostic(Guid.NewGuid(), job.Id, f.Project, f.Build, new string('a',40), "app", "runtime", "Crash",
            "password=do-not-export", f.Clock.Now, false);
        await f.CompleteAsync(evidence, new("Evidence", Evidence: new(job.Id, 1,
            [new(1, diagnostic, f.Clock.Now.AddDays(7))], false)));
        await f.Service.StopAsync(f.Org, f.Agent, job.Id, default);
        var stop = await f.PollAsync("stop"); await f.CompleteAsync(stop, new("Stopped"));
        Assert.Equal(PreviewPhase.Stopped, (await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default)).Phase);
        var retained = await f.Service.DiagnosticsAsync(f.Org, f.Agent, new(job.Id), default);
        Assert.DoesNotContain("do-not-export", Assert.Single(retained.Items).Diagnostic.Summary);
        Assert.NotNull((await f.Db.WebPreviewJobs.SingleAsync()).TeardownConfirmedAt);
    }

    [Fact]
    public async Task Admission_is_idempotent_and_uncertain_or_expired_jobs_keep_their_quota()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var first = await f.Service.StartAsync(f.Org, f.Agent, f.Request(), default);
        Assert.Equal(first.Id, (await f.Service.StartAsync(f.Org, f.Agent, f.Request(), default)).Id);
        Assert.Equal(1800, f.Grant.ReservedCpuSeconds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.StartAsync(f.Org, f.Agent,
            f.Request() with { Manifest = f.Request().Manifest with { LifetimeSeconds = 1200 } }, default));
        await f.Service.StartAsync(f.Org, f.Agent, f.Request("second"), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.StartAsync(f.Org, f.Agent, f.Request("third"), default));
        foreach (var job in await f.Db.WebPreviewJobs.ToListAsync()) job.ExpiresAt = f.Clock.Now.AddSeconds(-1);
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.StartAsync(f.Org, f.Agent, f.Request("third"), default));
    }

    [Fact]
    public async Task Self_reported_certification_cannot_dispatch_without_an_independent_trusted_release()
    {
        await using var f = new Fixture(); await f.SeedAsync(); f.Execution.ApprovedReleases.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.StartAsync(f.Org, f.Agent, f.Request(), default));
        Assert.Empty(await f.Db.WebPreviewJobs.ToListAsync()); Assert.Empty(await f.Db.WebHostCommands.ToListAsync());
    }

    [Fact]
    public async Task Late_initialization_cannot_restore_access_after_revocation_or_stop()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var (job, initialize) = await f.StartToInitializeAsync();
        await f.Service.StopAsync(f.Org, f.Agent, job.Id, default);
        await f.CompleteAsync(initialize, new("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready)));
        Assert.Equal(PreviewPhase.Stopping, (await f.Service.ReadAsync(f.Org, f.Agent, job.Id, default)).Phase);
        Assert.Null((await f.Db.WebPreviewJobs.SingleAsync()).TeardownConfirmedAt);
        var stop = await f.PollAsync("stop"); await f.CompleteAsync(stop, new("Stopped"));
        Assert.NotNull((await f.Db.WebPreviewJobs.SingleAsync()).TeardownConfirmedAt);
    }

    [Fact]
    public async Task Results_are_host_bound_idempotent_and_cannot_change_an_acknowledged_outcome()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.Service.StartAsync(f.Org, f.Agent, f.Request(), default);
        var upload = await f.PollAsync("upload");
        await f.CompleteAsync(upload, new("ArtifactReady"));
        await f.CompleteAsync(upload, new("ArtifactReady"));
        Assert.Single(await f.Db.WebHostCommands.Where(x => x.Action == "start").ToListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.CompleteAsync(upload, new("RejectedOrFailed")));
        var wrongPurpose = f.Message("heartbeat", new WebHostCommandPoll());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.PollAsync(wrongPurpose, default));
    }

    [Fact]
    public async Task Evidence_cannot_change_project_build_or_source_identity()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var (job, initialize) = await f.StartToInitializeAsync();
        await f.CompleteAsync(initialize, new("Completed", Guest: new(initialize.CommandId, "initialize", PreviewPhase.Ready)));
        var command = await f.PollAsync("evidence");
        var bad = new PreviewDiagnostic(Guid.NewGuid(), job.Id, Guid.NewGuid(), f.Build, new string('a',40), "app", "runtime", "Crash", "failure", f.Clock.Now, false);
        await f.CompleteAsync(command, new("Evidence", Evidence: new(job.Id, 1,
            [new(1, bad, f.Clock.Now.AddDays(7))], false)));
        Assert.Equal("Stopping", (await f.Db.WebPreviewJobs.SingleAsync()).Phase);
        Assert.Null((await f.Db.WebPreviewJobs.SingleAsync()).TeardownConfirmedAt);
        Assert.Null((await f.Db.WebHostCommands.SingleAsync(x => x.Id == command.CommandId)).ResponseJson);
        Assert.Empty(await f.Db.WebPreviewEvidence.ToListAsync());
    }
    [Fact]
    public async Task Container_build_inputs_preserve_only_verified_source_and_image_paths()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var bytes = Encoding.UTF8.GetBytes("FROM scratch");
        using var archive = new MemoryStream();
        using (var tar = new TarWriter(archive, leaveOpen: true))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/output/source/Dockerfile") { DataStream = new MemoryStream(bytes) });
        f.Store.Bytes = archive.ToArray();
        var build = await f.Db.DeliveryBuilds.SingleAsync();
        build.OutputsJson = JsonSerializer.Serialize(new[] { new BuildOutputManifestEntry("source/Dockerfile",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "text/plain", "web-file") });
        (await f.Db.ExecutionWorkloadAssignments.SingleAsync()).ResultArtifactDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(f.Store.Bytes));
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request = request with { Manifest = request.Manifest with { Mode = PreviewMode.Containers,
            Compose = JsonSerializer.SerializeToElement(new { services = new { app = new { build = new { context = "." } } } }), Entrypoint = new("app", 8080) } };
        await f.Service.StartAsync(f.Org, f.Agent, request, default);
        var upload = await f.PollAsync("upload");
        using var artifact = await f.Service.ArtifactAsync(f.Message("artifact", new WebHostArtifactRequest(upload.CommandId)), default);
        using var zip = new System.IO.Compression.ZipArchive(artifact.Content, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal("source/Dockerfile", Assert.Single(zip.Entries).FullName);
    }
}
