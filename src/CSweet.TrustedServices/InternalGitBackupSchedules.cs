using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitBackupJobs
{
    public async Task<InternalGitBackupSchedule> ScheduleAsync(Guid business, Guid repository, CancellationToken ct = default)
    {
        if (repository == Guid.Empty) throw new ArgumentException("Repository identity is required.");
        var path = Path.Combine(await DirectoryAsync(business, ct), repository.ToString("N") + ".schedule");
        return await ReadScheduleAsync(path, business, repository, ct);
    }

    public async Task<InternalGitBackupSchedule> SaveScheduleAsync(InternalGitBackupScheduleCommand command, CancellationToken ct = default)
    {
        var settings = command.Settings;
        if (command.RepositoryId == Guid.Empty || settings.IntervalHours is < 1 or > 8760 || settings.KeepLatest is < 1 or > 100 ||
            settings.Enabled && settings.KeepLatest is not null && !settings.ConfirmRetention)
            throw new ArgumentException("Choose a valid interval and retention count, and explicitly confirm automatic backup deletion.");
        var directory = await DirectoryAsync(command.OrganizationId, ct);
        var path = Path.Combine(directory, command.RepositoryId.ToString("N") + ".schedule");
        CheckPath(path); CheckPath(path + ".lock");
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = await ReadScheduleAsync(path, command.OrganizationId, command.RepositoryId, ct);
        if (current.Revision != settings.ExpectedRevision) throw new InvalidOperationException("Backup schedule changed; reload before saving.");
        var schedule = current with { Enabled = settings.Enabled, IntervalHours = settings.IntervalHours,
            KeepLatest = settings.KeepLatest, Revision = current.Revision + 1, LastWindow = null, LastJobId = null };
        await WriteScheduleAsync(path, schedule, ct); return schedule;
    }

    private static async Task<InternalGitBackupSchedule> ReadScheduleAsync(string path, Guid business, Guid repository, CancellationToken ct)
    {
        CheckPath(path);
        if (!File.Exists(path)) return new(business, repository, false, 24, null, 0);
        var schedule = JsonSerializer.Deserialize<InternalGitBackupSchedule>(await File.ReadAllTextAsync(path, ct)) ?? throw new IOException("Backup schedule is invalid.");
        if (schedule.OrganizationId != business || schedule.RepositoryId != repository || schedule.IntervalHours is < 1 or > 8760 ||
            schedule.KeepLatest is < 1 or > 100 || schedule.Revision <= 0) throw new IOException("Backup schedule identity or limits are invalid.");
        return schedule;
    }

    private static async Task WriteScheduleAsync(string path, InternalGitBackupSchedule schedule, CancellationToken ct)
    {
        var incoming = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, schedule, cancellationToken: ct); output.Flush(true); }
            File.Move(incoming, path, true);
        }
        finally { File.Delete(incoming); }
    }

    internal async Task ScheduleDueAsync(Guid business, CancellationToken ct)
    {
        var directory = await DirectoryAsync(business, ct);
        foreach (var path in Directory.EnumerateFiles(directory, "*.schedule"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var repository)) throw new IOException("Backup schedule filename is invalid.");
            CheckPath(path); CheckPath(path + ".lock");
            await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var schedule = await ReadScheduleAsync(path, business, repository, ct);
            if (!schedule.Enabled) continue;
            var window = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() / (schedule.IntervalHours * 3600L);
            if (schedule.LastWindow is { } previousWindow && window <= previousWindow) continue;
            // A crash between queueing and recording the window reuses this exact job identity.
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{business:N}:{repository:N}:{schedule.Revision}:{window}"));
            var id = new Guid(digest.AsSpan(0, 16));
            await QueueAsync(new(business, repository, id), ct);
            await WriteScheduleAsync(path, schedule with { LastWindow = window, LastJobId = id }, ct);
        }
    }

    private async Task ApplyRetentionAsync(InternalGitBackupJob job, CancellationToken ct)
    {
        var path = Path.Combine(await DirectoryAsync(job.OrganizationId, ct), job.RepositoryId.ToString("N") + ".schedule");
        CheckPath(path); CheckPath(path + ".lock");
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var schedule = await ReadScheduleAsync(path, job.OrganizationId, job.RepositoryId, ct);
        if (!schedule.Enabled || schedule.LastJobId != job.Id || schedule.KeepLatest is not { } keep) return;
        var backups = (await store.ListBackupsAsync(job.OrganizationId, ct)).Where(b => b.RepositoryId == job.RepositoryId)
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id).ToArray();
        if (!backups.Any(b => b.Id == job.Id)) throw new IOException("Retention requires a completed replacement backup.");
        // Always retain the successful scheduled replacement, even if clocks have changed.
        var retained = backups.Where(b => b.Id != job.Id).Take(keep - 1).Select(b => b.Id).Append(job.Id).ToHashSet();
        foreach (var backup in backups.Where(b => !retained.Contains(b.Id)))
            await store.DeleteBackupAsync(new(job.OrganizationId, job.RepositoryId, backup.Id), ct);
    }
}
