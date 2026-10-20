namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    private FileStream EnterBusinessStorage(Guid business)
    {
        VerifyRoot();
        return BusinessStorageLease.Enter(_options.RepositoryRoot, business);
    }
    private string BusinessTemporaryRoot(Guid business) =>
        OwnedStorage.Child(_options.TemporaryRoot, "businesses", business.ToString("N"));
    public async Task DeleteBusinessStorageAsync(Guid business, CancellationToken token = default)
    {
        VerifyRoot();
        using var lease = BusinessStorageLease.Enter(_options.RepositoryRoot, business, deleting: true);
        using var lfs = new InternalGitLfsStore(Microsoft.Extensions.Options.Options.Create(_options));
        using var backups = new InternalGitBackupStorage(_options);
        await lfs.DeleteBusinessAsync(business, token);
        await backups.DeleteBusinessAsync(business, token);
        OwnedStorage.DeleteDirectory(_options.TemporaryRoot, BusinessTemporaryRoot(business), token);
        OwnedStorage.DeleteDirectory(_options.RepositoryRoot,
            OwnedStorage.Child(_options.RepositoryRoot, business.ToString("N")), token);
        OwnedStorage.DeleteDirectory(_options.RepositoryRoot,
            OwnedStorage.Child(_options.RepositoryRoot, "csweet-backup-jobs", business.ToString("N")), token);
        // External GitHub repositories and shared App credentials are never removed.
    }
}
