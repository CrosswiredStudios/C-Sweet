using CSweet.Application.GenAi;
using CSweet.Application.PersonalAssistant;
using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Infrastructure.GenAi;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.TrustedServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Core;

public interface IBusinessComputeStorageCleanup
{
    Task<bool> TryCleanupAsync(Guid businessId, CancellationToken token);
}

/// <summary>Runs only while business deletion retains the authoritative storage locators.</summary>
public sealed class BusinessStorageCleanup(CSweetDbContext db, IMediaAssetStore media,
    IResumableMediaUploadStore uploads, ITrustedSourceControlHostClient git,
    IRepositorySourceStore sources, IOptions<AgentRuntimeManagerOptions> runtime,
    IBusinessComputeStorageCleanup compute)
{
    public async Task<bool> TryCleanupAsync(Guid businessId, CancellationToken token)
    {
        var status = await db.CoreOrganizations.AsNoTracking().Where(x => x.Id == businessId)
            .Select(x => (OrganizationStatus?)x.Status).SingleOrDefaultAsync(token);
        if (status is null) throw new InvalidOperationException("Business storage ownership records are unavailable.");
        if (status != OrganizationStatus.Deleting)
            throw new InvalidOperationException("Storage cleanup requires current business deletion intent.");

        foreach (var asset in await db.MediaAssets.AsNoTracking().Where(x => x.OrganizationId == businessId).ToListAsync(token))
        {
            // Old/imported rows can share a locator. The surviving business keeps its bytes.
            if (!await db.MediaAssets.AnyAsync(x => x.OrganizationId != businessId && x.StorageKey == asset.StorageKey, token))
                await media.DeleteAsync(asset.StorageKey, token);
        }
        foreach (var id in await db.MediaUploadSessions.Where(x => x.OrganizationId == businessId).Select(x => x.Id).ToListAsync(token))
            await uploads.DeleteAsync(id, token);
        var root = runtime.Value.WorkspaceSnapshotStorePath;
        OwnedStorage.DeleteDirectory(root, OwnedStorage.Child(root, businessId.ToString("N")), token);
        foreach (var draft in await db.RepositoryOnboardingDrafts.AsNoTracking()
                     .Where(x => x.OrganizationId == businessId).ToListAsync(token))
            await sources.DiscardAsync(draft.ApplicationUserId, draft.Id, token);

        // GitHost owns its filesystem/NAS/S3 stores. Never interpret a remote locator as an API-host path.
        await git.DeleteBusinessStorageAsync(businessId, token);
        return await compute.TryCleanupAsync(businessId, token);
    }
}
