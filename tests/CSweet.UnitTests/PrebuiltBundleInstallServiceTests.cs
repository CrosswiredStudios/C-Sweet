using System.Net;
using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class PrebuiltBundleInstallServiceTests
{
    internal static readonly byte[] BundleBytes = Encoding.UTF8.GetBytes("fake-csab-bundle-bytes");

    internal static string BundleDigest =>
        Convert.ToHexString(SHA256.HashData(BundleBytes)).ToLowerInvariant();

    [Fact]
    public async Task DefinitionImport_InstallsPrebuiltWithoutQueueingSourceBuild()
    {
        await using var db = CreateDbContext();
        var package = NewImportablePackage();
        db.AddRange(package.PackageSource!, package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, $"{BundleDigest}  {package.ReleaseAssetName}\n");
        var builds = new RecordingBuildService();
        var service = new AgentDefinitionService(
            db,
            new TestAuditEventWriter(),
            builds,
            prebuiltInstallService: CreateService(db, handler, new FakeArtifactStore()));

        var definition = await service.ImportAsync(package.Id, ImportRequest());

        Assert.Equal(AgentDefinitionStatus.Available.ToString(), definition.Status);
        Assert.Null(builds.QueuedPackageVersionId);
        Assert.Equal("PrebuiltRelease", definition.Build?.SourceMode);
        Assert.Equal("v1.11.1", definition.Build?.ReleaseTag);
        Assert.Equal(AgentPackageVersionStatus.Built, (await db.AgentPackageVersions.SingleAsync()).Status);
    }

    [Fact]
    public async Task DefinitionImport_FallsBackToSourceBuildWhenPrebuiltFails()
    {
        await using var db = CreateDbContext();
        var package = NewImportablePackage();
        db.AddRange(package.PackageSource!, package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Every download 404s, so the prebuilt install fails and the classic path runs.
        var handler = new BundleHandler(BundleBytes, Sidecar: null, FailDownloads: true);
        var builds = new RecordingBuildService();
        var service = new AgentDefinitionService(
            db,
            new TestAuditEventWriter(),
            builds,
            prebuiltInstallService: CreateService(db, handler, new FakeArtifactStore()));

        var definition = await service.ImportAsync(package.Id, ImportRequest());

        // The classic path creates the source-build job directly (no builder call in import).
        var jobs = await db.AgentBuildJobs.OrderBy(x => x.Attempt).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Equal(AgentBuildStatus.Failed, jobs[0].Status);
        Assert.Equal(AgentBuildStatus.Queued, jobs[1].Status);
        Assert.Equal(2, jobs[1].Attempt);
        Assert.Equal(AgentBuildStepKeys.Queued, AgentBuildStepStore.Read(jobs[1]).First().Key);
        Assert.Equal(AgentDefinitionStatus.Building.ToString(), definition.Status);
    }

    [Fact]
    public async Task DefinitionUpdate_InstallsPrebuiltWithoutQueueingSourceBuild()
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var current = NewImportablePackage();
        current.Version = "1.10.0";
        current.CommitSha = new string('c', 40);
        current.ManifestDigest = new string('d', 64);
        current.Status = AgentPackageVersionStatus.Built;
        current.PackageDigest = "sha256:" + new string('a', 64);
        current.ArtifactSignature = "current-signature";
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            PackageSourceId = current.PackageSourceId,
            AgentId = current.AgentId,
            PackageVersionId = current.Id,
            PackageVersion = current,
            Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true,
            DefaultActivationMode = ActivationMode.OnDemand,
            DefaultTickFrequencySeconds = 3600,
            DefaultOverlapPolicy = OverlapPolicy.Skip,
            DefaultMaxRuntimeSeconds = 600,
            DefaultMemoryMb = 1024,
            DefaultCpuPercent = 50,
            CreatedAt = now,
            UpdatedAt = now
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = definition.Id,
            SchemaVersion = "1",
            SettingsJson = "{}",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var update = NewImportablePackage();
        update.PackageSourceId = current.PackageSourceId;
        update.PackageSource = current.PackageSource;
        update.CommitSha = new string('e', 40);
        update.ManifestDigest = new string('f', 64);
        db.AddRange(current.PackageSource!, current, definition, update);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, $"{BundleDigest}  {update.ReleaseAssetName}\n");
        var builds = new RecordingBuildService();
        var service = new AgentDefinitionService(
            db,
            new TestAuditEventWriter(),
            builds,
            prebuiltInstallService: CreateService(db, handler, new FakeArtifactStore()));

        var result = await service.UpdateAsync(
            definition.Id, new CSweet.Contracts.Agents.UpdateAgentDefinitionRequest(update.Id));

        Assert.Equal("1.11.1", result.AgentVersion);
        Assert.Equal(AgentDefinitionStatus.Available.ToString(), result.Status);
        Assert.Null(builds.QueuedPackageVersionId);
        Assert.Equal("PrebuiltRelease", result.Build?.SourceMode);
        var jobs = await db.AgentBuildJobs.OrderBy(x => x.Attempt).ToListAsync();
        var job = Assert.Single(jobs);
        Assert.Equal(AgentBuildStatus.Succeeded, job.Status);
        Assert.Equal(
            [AgentBuildStepKeys.Queued, AgentBuildStepKeys.Download, AgentBuildStepKeys.Verify, AgentBuildStepKeys.Install],
            AgentBuildStepStore.Read(job).Select(x => x.Key).ToArray());
    }

    [Fact]
    public async Task InstallAsync_DownloadsVerifiesAndMarksBuilt()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, $"{BundleDigest}  {package.ReleaseAssetName}\n");
        var service = CreateService(db, handler, new FakeArtifactStore());

        var jobId = await service.InstallAsync(package.Id);

        var job = await db.AgentBuildJobs.SingleAsync(x => x.Id == jobId);
        Assert.Equal(AgentBuildStatus.Succeeded, job.Status);
        var refreshed = await db.AgentPackageVersions.SingleAsync(x => x.Id == package.Id);
        Assert.Equal(AgentPackageVersionStatus.Built, refreshed.Status);
        Assert.Equal("sha256:" + BundleDigest, refreshed.PackageDigest);
        Assert.Equal("sha256:" + BundleDigest, refreshed.ReleaseBundleDigest);
        Assert.Equal(AgentBuildStepStatuses.Succeeded, StepStatus(job, AgentBuildStepKeys.Download));
        Assert.Equal(AgentBuildStepStatuses.Succeeded, StepStatus(job, AgentBuildStepKeys.Verify));
        Assert.Equal(AgentBuildStepStatuses.Succeeded, StepStatus(job, AgentBuildStepKeys.Install));
    }

    [Fact]
    public async Task InstallAsync_SealFailureFailsInstallStepWithDetail()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, $"{BundleDigest}  {package.ReleaseAssetName}\n");
        var service = CreateService(db, handler, new SealFailingArtifactStore());

        var exception = await Assert.ThrowsAsync<AgentBuildException>(() => service.InstallAsync(package.Id));
        Assert.Contains("sealed", exception.Message, StringComparison.OrdinalIgnoreCase);
        var job = await db.AgentBuildJobs.SingleAsync();
        Assert.Equal(AgentBuildStatus.Failed, job.Status);
        var installStep = AgentBuildStepStore.Read(job).Single(x => x.Key == AgentBuildStepKeys.Install);
        Assert.Equal(AgentBuildStepStatuses.Failed, installStep.Status);
        Assert.Contains("sealed", installStep.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_RejectsDigestMismatch()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var wrongDigest = new string('0', 64);
        var handler = new BundleHandler(BundleBytes, $"{wrongDigest}  {package.ReleaseAssetName}\n");
        var service = CreateService(db, handler, new FakeArtifactStore());

        var exception = await Assert.ThrowsAsync<AgentBuildException>(() => service.InstallAsync(package.Id));
        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
        var job = await db.AgentBuildJobs.SingleAsync();
        Assert.Equal(AgentBuildStatus.Failed, job.Status);
        Assert.Equal(AgentPackageVersionStatus.Previewed, (await db.AgentPackageVersions.SingleAsync()).Status);
    }

    [Fact]
    public async Task InstallAsync_RefusesWithoutChecksumSidecar()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, Sidecar: null);
        var service = CreateService(db, handler, new FakeArtifactStore());

        await Assert.ThrowsAsync<AgentBuildException>(() => service.InstallAsync(package.Id));
        Assert.Equal(AgentBuildStatus.Failed, (await db.AgentBuildJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task InstallAsync_UsesStoredDigestWithoutSidecarLookup()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        package.ReleaseBundleDigest = "sha256:" + BundleDigest;
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, Sidecar: null);
        var service = CreateService(db, handler, new FakeArtifactStore());

        var jobId = await service.InstallAsync(package.Id);
        Assert.Equal(AgentBuildStatus.Succeeded, (await db.AgentBuildJobs.SingleAsync(x => x.Id == jobId)).Status);
        Assert.DoesNotContain(handler.Requests, url => url.EndsWith(".sha256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_RejectsMalformedBundle()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        db.AgentPackageVersions.Add(package);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, $"{BundleDigest}  {package.ReleaseAssetName}\n");
        var service = CreateService(db, handler, new RejectingArtifactStore());

        await Assert.ThrowsAsync<AgentBuildException>(() => service.InstallAsync(package.Id));
        Assert.Equal(AgentBuildStatus.Failed, (await db.AgentBuildJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task InstallAsync_IsIdempotentWhenAlreadyBuilt()
    {
        await using var db = CreateDbContext();
        var package = NewPackage();
        package.Status = AgentPackageVersionStatus.Built;
        package.PackageDigest = "sha256:" + BundleDigest;
        db.AgentPackageVersions.Add(package);
        var job = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            Attempt = 1,
            QueuedAt = DateTimeOffset.UtcNow,
            StepsJson = AgentBuildStepStore.CreatePrebuiltInitialJson(DateTimeOffset.UtcNow)
        };
        db.AgentBuildJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var handler = new BundleHandler(BundleBytes, Sidecar: null);
        var service = CreateService(db, handler, new FakeArtifactStore());

        var jobId = await service.InstallAsync(package.Id);
        Assert.Equal(job.Id, jobId);
        Assert.Empty(handler.Requests);
    }

    private static AgentPackageVersion NewPackage()
    {
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = "https://github.com/example/agent",
            RepositoryOwner = "example",
            RepositoryName = "agent",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return new()
        {
            Id = Guid.NewGuid(),
            PackageSourceId = source.Id,
            PackageSource = source,
            AgentId = "com.example.agent",
            AgentName = "Example",
            Version = "1.11.1",
            CommitSha = new string('c', 40),
            ManifestDigest = new string('d', 64),
            ReleaseTag = "v1.11.1",
            ReleaseAssetName = "agent-1.11.1-linux-x64.csab",
            ReleaseAssetUrl = "https://github.com/example/agent/releases/download/v1.11.1/agent.csab",
            Status = AgentPackageVersionStatus.Previewed
        };
    }

    private static CSweetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AgentPackageVersion NewImportablePackage()
    {
        var package = NewPackage();
        package.ManifestJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            manifestVersion = "2.0",
            kind = "agent",
            id = "com.example.agent",
            name = "Example Agent",
            version = "1.11.1",
            publisher = new { id = "example", name = "Example" },
            runtime = new
            {
                type = "dotnet-project",
                projectPath = "src/Agent.csproj",
                targetFramework = "net10.0",
                defaultActivationMode = "OnDemand"
            },
            protocol = new { minimumVersion = "2.0", maximumVersion = "2.x" },
            provides = Array.Empty<object>(),
            requires = Array.Empty<object>(),
            events = new { subscribes = Array.Empty<string>() },
            configuration = Array.Empty<object>(),
            credentials = Array.Empty<object>(),
            webAccess = new { mode = "None", rules = Array.Empty<object>() }
        });
        return package;
    }

    private static CSweet.Contracts.Agents.InstallAgentRequest ImportRequest() => new(
        "ignored-global-definition", "OnDemand", 3600, "Skip", [], [], [], [], [], 600, 1024, 50);

    private static PrebuiltBundleInstallService CreateService(
        CSweetDbContext db,
        BundleHandler handler,
        IAgentArtifactStore store) => new(
        db,
        new HttpClient(handler),
        store,
        new NullRepositoryClient(),
        new TestAuditEventWriter(),
        NullLogger<PrebuiltBundleInstallService>.Instance);

    internal static PrebuiltBundleInstallService CreateServiceForRecovery(
        CSweetDbContext db,
        HttpMessageHandler handler) => new(
        db,
        new HttpClient(handler),
        new FakeArtifactStore(),
        new NullRepositoryClient(),
        new TestAuditEventWriter(),
        NullLogger<PrebuiltBundleInstallService>.Instance);

    private static string? StepStatus(AgentBuildJob job, string key) =>
        AgentBuildStepStore.Read(job).FirstOrDefault(x => x.Key == key)?.Status;

    private sealed class BundleHandler(byte[] bundle, string? Sidecar, bool FailDownloads = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            if (request.RequestUri?.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(Sidecar is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sidecar) });
            }
            if (FailDownloads)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bundle)
            });
        }
    }

    internal sealed class BundleHandlerForRecovery(byte[] bundle, string? Sidecar) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(Sidecar is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sidecar) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bundle)
            });
        }
    }

    private sealed class FakeArtifactStore : IAgentArtifactStore
    {
        public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentArtifactReference> ImportAsync(
            Stream content,
            ArtifactImportDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            // Mirrors FileSystemAgentArtifactStore: the digest gate fails closed.
            // The store speaks "sha256:<hex>" digests.
            using var sha = System.Security.Cryptography.SHA256.Create();
            var actual = "sha256:" + Convert.ToHexString(sha.ComputeHash(content)).ToLowerInvariant();
            if (!string.Equals(actual, descriptor.ExpectedDigest, StringComparison.Ordinal))
                throw new InvalidDataException("The exported artifact digest did not match the declared digest.");
            return Task.FromResult(new AgentArtifactReference(
                actual, "signature", "csab/1", "linux", "x64"));
        }
    }

    private sealed class RejectingArtifactStore : IAgentArtifactStore
    {
        public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentArtifactReference> ImportAsync(
            Stream content,
            ArtifactImportDescriptor descriptor,
            CancellationToken cancellationToken = default) =>
            throw new InvalidDataException("not a bundle");
    }

    private sealed class SealFailingArtifactStore : IAgentArtifactStore
    {
        public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentArtifactReference> ImportAsync(
            Stream content,
            ArtifactImportDescriptor descriptor,
            CancellationToken cancellationToken = default) =>
            // Digest and bundle validation passed; the seal into the store failed.
            throw new IOException("The artifact store disk is full.");
    }

    private sealed class RecordingBuildService : IAgentBuildService
    {
        public Guid? QueuedPackageVersionId { get; private set; }

        public Task<Guid> QueueAsync(Guid packageVersionId, CancellationToken cancellationToken = default)
        {
            QueuedPackageVersionId = packageVersionId;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class NullRepositoryClient : IGitHubAgentRepositoryClient
    {
        public Task<string> GetDefaultBranchAsync(string owner, string name, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string> ResolveCommitShaAsync(string owner, string name, string reference, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<byte[]> GetRootManifestAsync(string owner, string name, string sha, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string owner, string name, CancellationToken ct) =>
            Task.FromResult<GitHubReleaseInfo?>(new GitHubReleaseInfo(
                "v1.11.1",
                false,
                false,
                [
                    new GitHubReleaseAssetInfo(
                        "agent-1.11.1-linux-x64.csab",
                        "https://github.com/example/agent/releases/download/v1.11.1/agent.csab",
                        10),
                    new GitHubReleaseAssetInfo(
                        "agent-1.11.1-linux-x64.csab.sha256",
                        "https://github.com/example/agent/releases/download/v1.11.1/agent.csab.sha256",
                        80)
                ]));
    }
}
