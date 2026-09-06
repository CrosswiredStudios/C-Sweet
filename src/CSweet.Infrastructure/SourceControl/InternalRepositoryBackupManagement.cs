using CSweet.Contracts.SourceControl;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class InternalRepositoryManagementService
{
    public async Task<InternalGitBackupSchedule> BackupScheduleAsync(Guid business, Guid user, Guid repository, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct); _ = await FindAsync(business, repository, ct);
        return await host.InternalBackupScheduleAsync(business, repository, ct);
    }
    public async Task<InternalGitBackupSchedule> SaveBackupScheduleAsync(Guid business, Guid user, Guid repositoryId, SaveInternalGitBackupSchedule request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct); var repository = await FindAsync(business, repositoryId, ct);
        if (request.Enabled && repository.Status is not (SourceControlRepositoryStatus.Ready or SourceControlRepositoryStatus.Archived))
            throw new InvalidOperationException("Repository must be ready or archived to enable scheduled backups.");
        await AuditAsync(business, user, repositoryId, "BackupSchedule", "Started", request, ct);
        var result = await host.SaveInternalBackupScheduleAsync(new(business, repositoryId, request), ct);
        await AuditAsync(business, user, repositoryId, "BackupSchedule", "Completed", result, ct); return result;
    }
    public async Task<bool> DismissBackupJobAsync(Guid business, Guid user, Guid id, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        await AuditAsync(business, user, id, "BackupJobDismiss", "Started", new { id }, ct);
        await host.DismissInternalBackupJobAsync(business, id, ct);
        await AuditAsync(business, user, id, "BackupJobDismiss", "Completed", new { id }, ct);
        return true;
    }
    public async Task<IReadOnlyList<InternalGitBackupJob>> BackupJobsAsync(Guid business, Guid user, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        return await host.ListInternalBackupJobsAsync(business, ct);
    }
    public async Task<InternalGitBackupJob> QueueBackupAsync(Guid business, Guid user, Guid repositoryId, CreateInternalGitBackupRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var repository = await FindAsync(business, repositoryId, ct);
        if (repository.Status is not (SourceControlRepositoryStatus.Ready or SourceControlRepositoryStatus.Archived)) throw new InvalidOperationException("Repository must be ready or archived before backup.");
        if (request.BackupId == Guid.Empty) throw new ArgumentException("Backup identity is required.");
        await AuditAsync(business, user, repositoryId, "BackupQueue", "Started", request, ct);
        var result = await host.QueueInternalBackupAsync(new(business, repositoryId, request.BackupId), ct);
        await AuditAsync(business, user, repositoryId, "BackupQueue", "Completed", result, ct);
        return result;
    }
    public async Task<IReadOnlyList<InternalGitBackupSummary>> BackupsAsync(Guid business, Guid user, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        return await host.ListInternalBackupsAsync(business, ct);
    }
    public async Task<InternalGitBackupSummary> BackupAsync(Guid business, Guid user, Guid repositoryId, CreateInternalGitBackupRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var repository = await FindAsync(business, repositoryId, ct);
        if (repository.Status is not (SourceControlRepositoryStatus.Ready or SourceControlRepositoryStatus.Archived)) throw new InvalidOperationException("Repository must be ready or archived before backup.");
        if (request.BackupId == Guid.Empty) throw new ArgumentException("Backup identity is required.");
        await AuditAsync(business, user, repositoryId, "Backup", "Started", request, ct);
        var result = await host.CreateInternalBackupAsync(new(business, repositoryId, request.BackupId), ct);
        await AuditAsync(business, user, repositoryId, "Backup", "Completed", result, ct);
        return result;
    }
    public async Task<bool> DeleteBackupAsync(Guid business, Guid user, Guid repository, Guid backup, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        await AuditAsync(business, user, repository, "BackupDelete", "Started", new { backup }, ct);
        await host.DeleteInternalBackupAsync(new(business, repository, backup), ct);
        await AuditAsync(business, user, repository, "BackupDelete", "Completed", new { backup }, ct);
        return true;
    }
    public async Task<SourceControlRepositorySummary> RestoreBackupAsync(Guid business, Guid user, Guid sourceRepository, Guid backup,
        RestoreInternalGitBackupRequest request, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        var name = ValidateName(request.Name);
        if (request.RestoreId == Guid.Empty || request.RestoreId == sourceRepository) throw new ArgumentException("Restore requires a new repository identity.");
        var connection = await EnsureConnectionAsync(business, ct);
        var canonical = $"internal/{business:N}/{name.ToLowerInvariant()}";
        var repository = await db.SourceControlRepositories.AsTracking().SingleOrDefaultAsync(r => r.Id == request.RestoreId, ct);
        if (repository is not null && (repository.OrganizationId != business || repository.ConnectionId != connection.Id || repository.CanonicalPath != canonical))
            throw new InvalidOperationException("Restore identity is already in use by another repository.");
        if (await db.SourceControlRepositories.AnyAsync(r => r.OrganizationId == business && r.CanonicalPath == canonical && r.Id != request.RestoreId, ct))
            throw new ArgumentException("Choose a new repository name for the restore.");
        if (repository is null)
        {
            repository = new() { Id = request.RestoreId, OrganizationId = business, ConnectionId = connection.Id, Name = name, CanonicalPath = canonical,
                Owner = business.ToString("N"), ExternalRepositoryId = request.RestoreId.ToString("N"), ProviderRepositoryKey = $"internal:{request.RestoreId:N}",
                IsPrivate = true, IsManaged = true, Status = SourceControlRepositoryStatus.Provisioning, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            db.SourceControlRepositories.Add(repository); await db.SaveChangesAsync(ct);
        }
        await AuditAsync(business, user, repository.Id, "BackupRestore", "Started", new { sourceRepository, backup }, ct);
        var result = await host.RestoreInternalBackupAsync(new(business, sourceRepository, backup, request.RestoreId), ct);
        if (repository.Status == SourceControlRepositoryStatus.Provisioning)
        {
            repository.DefaultBranch = result.DefaultBranch; repository.Status = SourceControlRepositoryStatus.Ready; repository.Revision++;
            repository.UpdatedAt = clock.GetUtcNow(); repository.LastVerifiedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        await AuditAsync(business, user, repository.Id, "BackupRestore", "Completed", new { sourceRepository, backup }, ct);
        return Summary(repository);
    }
}
