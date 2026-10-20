using CSweet.Application.Core;
using CSweet.Application.GenAi;
using CSweet.Application.Setup;
using CSweet.Application.SourceControl;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.GenAi;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.PersonalAssistant;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.TrustedServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class ResourceCleanupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "csweet-cleanup-tests", Guid.NewGuid().ToString("N"));
    private readonly DbContextOptions<CSweetDbContext> options = new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeCleanup_RetainsDiscoveryUntilConfirmed_IncludingStartupWithoutHandle(bool hasHandle)
    {
        await using var db = new CSweetDbContext(options);
        var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = Guid.NewGuid().ToString(), IsEnabled = false };
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = installation.Id, TickId = Guid.NewGuid() };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        var assignment = new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(), AgentRuntimeInstanceId = runtime.Id, BusinessId = installation.BusinessId,
            Status = ExecutionAssignmentStatus.Running
        };
        if (hasHandle) { runtime.IsolationProviderId = "execution-fleet"; runtime.ProviderInstanceId = assignment.Id.ToString("N"); }
        db.AddRange(installation, runtime, assignment);
        await ResourceCleanupQueue.EnqueueAsync(db, ResourceCleanupQueue.Runtime, installation.Id,
            Guid.Parse(installation.BusinessId), DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        var runner = new ControlledRunner();
        var cleanup = new InstallationRuntimeCleanup(db, runner);
        Assert.False(await cleanup.TryCleanupAsync(installation.Id, default));
        Assert.True(await db.ExecutionWorkloadAssignments.AnyAsync(x => x.Id == assignment.Id));
        Assert.Equal(AgentRuntimeStatus.Stopping, runtime.Status);
        if (hasHandle) Assert.NotNull(runtime.ProviderInstanceId);

        // A new context represents recovery after restart; retained rows still identify the exact attempt.
        await using var recovered = new CSweetDbContext(options);
        runner.Confirmed = true;
        Assert.True(await new InstallationRuntimeCleanup(recovered, runner).TryCleanupAsync(installation.Id, default));
        var stopped = await recovered.AgentRuntimeInstances.SingleAsync();
        Assert.Equal(AgentRuntimeStatus.Cancelled, stopped.Status);
        Assert.Null(stopped.ProviderInstanceId);
        Assert.All(runner.Destroyed, handle => Assert.Equal(assignment.Id.ToString("N"), handle.ProviderInstanceId));
    }

    [Fact]
    public async Task StorageDeletion_RetriesAfterRestart_AndBusinessCannotPurgeBeforeConfirmation()
    {
        Directory.CreateDirectory(root);
        var business = new Organization { Id = Guid.NewGuid(), Name = "Delete", Status = OrganizationStatus.Active };
        var retained = new Organization { Id = Guid.NewGuid(), Name = "Retain", Status = OrganizationStatus.Active };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CSweet:GenAi:MediaRoot"] = Path.Combine(root, "media") }).Build();
        var store = new FileMediaAssetStore(config, Options.Create(new MediaAssetStorageOptions()));
        var saved = await store.SaveAsync("owned.bin", new MemoryStream([1, 2, 3]));
        var shared = await store.SaveAsync("shared.bin", new MemoryStream([4, 5, 6]));
        var snapshotRoot = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(Path.Combine(snapshotRoot, business.Id.ToString("N")));
        File.WriteAllText(Path.Combine(snapshotRoot, business.Id.ToString("N"), "snapshot.zip"), "owned");
        Directory.CreateDirectory(Path.Combine(snapshotRoot, retained.Id.ToString("N")));
        var keptSnapshot = Path.Combine(snapshotRoot, retained.Id.ToString("N"), "snapshot.zip");
        File.WriteAllText(keptSnapshot, "kept");
        var git = new CleanupGit { Fail = true };
        await using (var db = new CSweetDbContext(options))
        {
            db.AddRange(business, retained,
                new MediaAsset { Id = Guid.NewGuid(), OrganizationId = business.Id, StorageKey = saved.StorageKey },
                new MediaAsset { Id = Guid.NewGuid(), OrganizationId = business.Id, StorageKey = shared.StorageKey },
                new MediaAsset { Id = Guid.NewGuid(), OrganizationId = retained.Id, StorageKey = shared.StorageKey });
            await db.SaveChangesAsync();
            var storage = Storage(db, config, store, git, snapshotRoot);
            var service = Organizations(db, storage);
            var response = await service.DeleteAsync(business.Id);
            Assert.Equal("storage_cleanup_pending", response.ErrorCode);
            var processor = Processor(db, storage);
            await processor.RunOnceAsync();
            var pending = await db.ResourceCleanupRequests.SingleAsync();
            Assert.Null(pending.CompletedAt);
            Assert.Equal(nameof(IOException), pending.LastError);
            Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == business.Id));
            pending.AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        git.Fail = false;
        await using (var db = new CSweetDbContext(options))
        {
            var storage = Storage(db, config, store, git, snapshotRoot);
            await Processor(db, storage).RunOnceAsync();
            Assert.NotNull((await db.ResourceCleanupRequests.SingleAsync()).CompletedAt);
            Assert.True((await Organizations(db, storage).DeleteAsync(business.Id)).Succeeded);
            Assert.False(await db.CoreOrganizations.AnyAsync(x => x.Id == business.Id));
            Assert.True(await db.CoreOrganizations.AnyAsync(x => x.Id == retained.Id));
            Assert.True(File.Exists(keptSnapshot));
            Assert.False(File.Exists(Path.Combine(root, "media", saved.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
            await using var kept = await store.OpenReadAsync(shared.StorageKey);
            Assert.Equal(3, kept.Length);
            Assert.NotNull((await db.ResourceCleanupRequests.SingleAsync()).CompletedAt);
        }
    }

    [Fact]
    public async Task GitCleanup_RemovesOnlyBusinessPrefixes_AndFencesBackupsAndLateWrites()
    {
        var repositoryRoot = Path.Combine(root, "repositories");
        Directory.CreateDirectory(repositoryRoot);
        File.WriteAllText(Path.Combine(repositoryRoot, ".csweet-git-store"), "fixture");
        var storage = new InternalGitStorageOptions { RepositoryRoot = repositoryRoot, ExpectedStoreId = "fixture", TemporaryRoot = Path.Combine(root, "temporary") };
        var store = new InternalGitRepositoryStore(Options.Create(storage));
        var business = Guid.NewGuid(); var other = Guid.NewGuid(); var repository = Guid.NewGuid();
        var owned = Seed(Path.Combine(repositoryRoot, business.ToString("N"), repository.ToString("N") + ".git"));
        var kept = Seed(Path.Combine(repositoryRoot, other.ToString("N"), repository.ToString("N") + ".git"));
        var lfs = Seed(Path.Combine(root, "lfs", storage.Lfs.KeyPrefix, business.ToString("N"), "repo"));
        var backup = Seed(Path.Combine(root, "backups", storage.Backup.KeyPrefix, business.ToString("N"), "repo"));
        var otherLfs = Seed(Path.Combine(root, "lfs", storage.Lfs.KeyPrefix, other.ToString("N"), "repo"));
        var jobs = Seed(Path.Combine(repositoryRoot, "csweet-backup-jobs", business.ToString("N")));
        var crashedTemporary = Seed(Path.Combine(storage.TemporaryRoot, "businesses", business.ToString("N"), "interrupted-import"));
        var otherTemporary = Seed(Path.Combine(storage.TemporaryRoot, "businesses", other.ToString("N"), "interrupted-import"));
        using (BusinessStorageLease.Enter(repositoryRoot, business))
            await Assert.ThrowsAsync<IOException>(() => store.DeleteBusinessStorageAsync(business));
        Assert.True(File.Exists(owned));
        await store.DeleteBusinessStorageAsync(business);
        await store.DeleteBusinessStorageAsync(business); // Lost response / duplicate delivery.
        Assert.False(File.Exists(owned)); Assert.False(File.Exists(lfs)); Assert.False(File.Exists(backup)); Assert.False(File.Exists(jobs));
        Assert.True(File.Exists(kept)); Assert.True(File.Exists(otherLfs));
        Assert.False(File.Exists(crashedTemporary)); Assert.True(File.Exists(otherTemporary));
        Assert.Throws<IOException>(() => BusinessStorageLease.Enter(repositoryRoot, business));
        Assert.Throws<IOException>(() => OwnedStorage.DeleteDirectory(repositoryRoot, Path.Combine(root, "repositories-other")));
    }

    [Fact]
    public async Task Retirement_IsScopedToEnrolledNode_AndCannotCompleteWithoutReceiptOrTeardown()
    {
        await using var db = new CSweetDbContext(options);
        var business = new Organization { Id = Guid.NewGuid(), Status = OrganizationStatus.Deleting };
        var setup = new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = business.Id, State = "Ready" };
        db.AddRange(business, setup, new ComputeNodeRegistration { Id = setup.Id, OrganizationId = business.Id, ProviderId = "hyperv" });
        await ResourceCleanupQueue.EnqueueAsync(db, ResourceCleanupQueue.BusinessStorage, business.Id, business.Id, DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();
        var service = new ComputeProviderRetirementService(db, new EphemeralDataProtectionProvider());
        Assert.False(await service.TryCleanupAsync(business.Id, default));
        var node = new ComputeProviderWorkRequest(business.Id, setup.Id, "hyperv", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Null(await service.ReadAsync(node with { NodeId = Guid.NewGuid() }, default));
        var grant = Assert.IsType<ComputeProviderRetirement>(await service.ReadAsync(node, default));
        setup.State = "Running";
        await db.SaveChangesAsync();
        Assert.False(await service.AuthorizeAsync(new(grant.CleanupId, grant.ReceiptToken), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(new(grant.CleanupId, grant.ReceiptToken), default));
        setup.State = "Failed";
        await db.SaveChangesAsync();
        Assert.True(await service.AuthorizeAsync(new(grant.CleanupId, grant.ReceiptToken), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ConfirmAsync(new(grant.CleanupId, new string('0', 64)), default));
        Assert.False(await service.TryCleanupAsync(business.Id, default));
        await service.ConfirmAsync(new(grant.CleanupId, grant.ReceiptToken), default);
        await service.ConfirmAsync(new(grant.CleanupId, grant.ReceiptToken), default);
        Assert.True(await service.TryCleanupAsync(business.Id, default));
    }

    private BusinessStorageCleanup Storage(CSweetDbContext db, IConfiguration config, IMediaAssetStore media, CleanupGit git, string snapshots) =>
        new(db, media, new FileResumableMediaUploadStore(config), git,
            new RepositorySourceStore(Options.Create(new RepositoryOnboardingOptions { StagingPath = Path.Combine(root, "staging") }), git),
            Options.Create(new AgentRuntimeManagerOptions { WorkspaceSnapshotStorePath = snapshots }), new ConfirmedCompute());
    private static ResourceCleanupProcessor Processor(CSweetDbContext db, BusinessStorageCleanup storage) =>
        new(db, new InstallationRuntimeCleanup(db, new ControlledRunner()), storage, NullLogger<ResourceCleanupProcessor>.Instance);
    private static CoreOrganizationService Organizations(CSweetDbContext db, BusinessStorageCleanup storage)
    {
        var audit = new TestAuditEventWriter();
        return new(db, audit, new RoleService(db, audit), new OrganizationDataPurgeService(db,
            new QuiescedAgents(), NullLogger<OrganizationDataPurgeService>.Instance, storageCleanup: storage));
    }
    private static string Seed(string directory) { Directory.CreateDirectory(directory); var path = Path.Combine(directory, "data"); File.WriteAllText(path, "fixture"); return path; }
    public void Dispose() { if (Directory.Exists(root)) OwnedStorage.DeleteDirectory(Path.GetDirectoryName(root)!, root); }
    private sealed class ConfirmedCompute : IBusinessComputeStorageCleanup { public Task<bool> TryCleanupAsync(Guid business, CancellationToken token) => Task.FromResult(true); }
    private sealed class QuiescedAgents : IBusinessAgentInstallationCleanup { public Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class CleanupGit : ITrustedSourceControlHostClient
    {
        public bool Fail;
        public Task DeleteBusinessStorageAsync(Guid business, CancellationToken token = default) => Fail ? Task.FromException(new IOException("Unavailable storage")) : Task.CompletedTask;
        public Task<TrustedInstallationDescriptor> DescribeInstallationAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedRepositoryDescriptor>> ListRepositoriesAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TrustedMergeResult> MergeAsync(TrustedMergeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TrustedWorkspaceSnapshot> PrepareWorkspaceAsync(TrustedWorkspaceSnapshotRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class ControlledRunner : IAgentWorkloadRunner
    {
        public bool Confirmed;
        public List<IsolationWorkloadHandle> Destroyed { get; } = [];
        public Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpecification specification, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) { Destroyed.Add(handle); return Task.CompletedTask; }
        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default) => Task.FromResult<IsolationWorkloadStatus?>(new(handle, Confirmed ? IsolationWorkloadState.Stopped : IsolationWorkloadState.Stopping, IsolationTerminationReason.Cancelled, null, null, null, null, null));
        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) => Task.FromResult("");
    }
}
