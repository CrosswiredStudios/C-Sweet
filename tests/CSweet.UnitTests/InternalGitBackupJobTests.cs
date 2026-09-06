using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InternalGitBackupJobTests : IDisposable
{
    [Fact]
    public async Task ScheduleRequiresRetentionApprovalQueuesOnceAndRetainsReplacementInRepositoryScope()
    {
        var business = Guid.NewGuid(); var repository = Guid.NewGuid(); var other = Guid.NewGuid();
        await store.ExecuteAsync(new(business, repository, "create", "main"));
        await store.ExecuteAsync(new(business, other, "create", "main"));
        await store.CreateBackupAsync(new(business, repository, Guid.NewGuid()));
        await store.CreateBackupAsync(new(business, other, Guid.NewGuid()));
        var clock = new Clock(); var jobs = new InternalGitBackupJobs(store, options, clock);
        Assert.False((await jobs.ScheduleAsync(business, repository)).Enabled);
        await Assert.ThrowsAsync<ArgumentException>(() => jobs.SaveScheduleAsync(new(business, repository, new(true, 1, 1, 0))));
        var schedule = await jobs.SaveScheduleAsync(new(business, repository, new(true, 1, 1, 0, true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.SaveScheduleAsync(new(business, repository, new(false, 1, null, 0))));
        await jobs.ScheduleDueAsync(business, default); await jobs.ScheduleDueAsync(business, default);
        var first = Assert.Single(await jobs.ListAsync(business));
        Assert.Equal(2, (await store.ListBackupsAsync(business)).Count);
        await jobs.ProcessAsync(business, first.Id);
        var backups = await store.ListBackupsAsync(business);
        Assert.Equal(first.Id, Assert.Single(backups, x => x.RepositoryId == repository).Id);
        Assert.Single(backups, x => x.RepositoryId == other);
        clock.Now = clock.Now.AddHours(1);
        await new InternalGitBackupJobs(store, options, clock).ScheduleDueAsync(business, default);
        Assert.Equal(2, (await jobs.ListAsync(business)).Count);
        await jobs.SaveScheduleAsync(new(business, repository, new(false, 1, 1, schedule.Revision)));
        clock.Now = clock.Now.AddHours(1); await jobs.ScheduleDueAsync(business, default);
        Assert.Equal(2, (await jobs.ListAsync(business)).Count);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 6, 1, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task FailedReplacementAndDisabledScheduleNeverDeleteExistingBackups()
    {
        var business = Guid.NewGuid(); var repository = Guid.NewGuid();
        await store.ExecuteAsync(new(business, repository, "create", "main"));
        var original = Guid.NewGuid(); await store.CreateBackupAsync(new(business, repository, original));
        var jobs = new InternalGitBackupJobs(store, options, new Clock());
        var schedule = await jobs.SaveScheduleAsync(new(business, repository, new(true, 1, 1, 0, true)));
        await jobs.ScheduleDueAsync(business, default);
        var job = Assert.Single(await jobs.ListAsync(business));
        var repositoryPath = Path.Combine(options.Value.RepositoryRoot, business.ToString("N"), repository.ToString("N") + ".git");
        var unavailable = repositoryPath + ".unavailable";
        Directory.Move(repositoryPath, unavailable);
        try
        {
            await jobs.ProcessAsync(business, job.Id);
            Assert.Equal("Failed", Assert.Single(await jobs.ListAsync(business)).Status);
            Assert.Equal(original, Assert.Single(await store.ListBackupsAsync(business)).Id);
        }
        finally { Directory.Move(unavailable, repositoryPath); }
        await jobs.SaveScheduleAsync(new(business, repository, new(false, 1, 1, schedule.Revision)));
        await jobs.QueueAsync(new(business, repository, job.Id));
        await jobs.ProcessAsync(business, job.Id);
        Assert.Equal("Completed", Assert.Single(await jobs.ListAsync(business)).Status);
        Assert.Equal(2, (await store.ListBackupsAsync(business)).Count);
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "csweet-backup-jobs-test-" + Guid.NewGuid().ToString("N"));
    private readonly IOptions<InternalGitStorageOptions> options;
    private readonly InternalGitRepositoryStore store;
    public InternalGitBackupJobTests()
    {
        var repositories = Path.Combine(root, "repositories"); Directory.CreateDirectory(repositories);
        File.WriteAllText(Path.Combine(repositories, ".csweet-git-store"), "jobs-test");
        options = Options.Create(new InternalGitStorageOptions { RepositoryRoot = repositories, ExpectedStoreId = "jobs-test", TemporaryRoot = Path.Combine(root, "temp") });
        store = new(options);
    }

    [Fact]
    public async Task RestartedWorkerCompletesQueuedBackupAndReplayDoesNotDuplicateIt()
    {
        var request = new InternalGitBackupRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await store.ExecuteAsync(new(request.OrganizationId, request.RepositoryId, "create", "main"));
        var jobs = new InternalGitBackupJobs(store, options);
        Assert.Equal("Queued", (await jobs.QueueAsync(request)).Status);
        Assert.Equal(request.BackupId, (await jobs.QueueAsync(request)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.DismissAsync(request.OrganizationId, request.BackupId));
        Assert.Empty(await jobs.ListAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.QueueAsync(request with { RepositoryId = Guid.NewGuid() }));
        var restarted = new InternalGitBackupJobs(store, options);
        await restarted.ProcessAsync(request.OrganizationId, request.BackupId);
        Assert.Equal("Completed", Assert.Single(await restarted.ListAsync(request.OrganizationId)).Status);
        await restarted.ProcessAsync(request.OrganizationId, request.BackupId);
        Assert.Equal("Completed", (await restarted.QueueAsync(request)).Status);
        Assert.Single(await store.ListBackupsAsync(request.OrganizationId));
        await jobs.DismissAsync(request.OrganizationId, request.BackupId);
        Assert.Empty(await jobs.ListAsync(request.OrganizationId)); Assert.Single(await store.ListBackupsAsync(request.OrganizationId));
    }

    [Fact]
    public async Task MissingRepositoryFailureCanBeRetriedWithSameBackupIdentity()
    {
        var request = new InternalGitBackupRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var jobs = new InternalGitBackupJobs(store, options);
        await jobs.QueueAsync(request); await jobs.ProcessAsync(request.OrganizationId, request.BackupId);
        var failed = Assert.Single(await jobs.ListAsync(request.OrganizationId)); Assert.Equal("Failed", failed.Status);
        Assert.DoesNotContain(root, failed.FailureMessage!);
        await store.ExecuteAsync(new(request.OrganizationId, request.RepositoryId, "create", "main"));
        Assert.Equal("Queued", (await jobs.QueueAsync(request)).Status);
        await jobs.ProcessAsync(request.OrganizationId, request.BackupId);
        Assert.Equal("Completed", Assert.Single(await jobs.ListAsync(request.OrganizationId)).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
