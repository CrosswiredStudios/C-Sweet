using System.Text.Json;
using CSweet.Contracts.SourceControl;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitBackupJobs(InternalGitRepositoryStore store, IOptions<InternalGitStorageOptions> options, TimeProvider? timeProvider = null)
{
    private string Root => Path.Combine(options.Value.RepositoryRoot, "csweet-backup-jobs");

    private async Task<string> DirectoryAsync(Guid business, CancellationToken ct)
    {
        if (business == Guid.Empty) throw new ArgumentException("Business identity is required.");
        if (!(await store.StatusAsync(ct)).Ready) throw new IOException("Repository storage is unavailable.");
        CheckPath(Root, true); Directory.CreateDirectory(Root);
        var directory = Path.Combine(Root, business.ToString("N")); CheckPath(directory, true); Directory.CreateDirectory(directory);
        return directory;
    }

    private static void CheckPath(string path, bool directory = false)
    {
        FileSystemInfo entry = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
            entry is FileInfo file && file.Exists && file.Length > 8192)
            throw new IOException("Backup job metadata is invalid.");
    }

    public async Task<InternalGitBackupJob> QueueAsync(InternalGitBackupRequest request, CancellationToken ct = default)
    {
        if (request.RepositoryId == Guid.Empty || request.BackupId == Guid.Empty) throw new ArgumentException("Repository and backup identities are required.");
        var directory = await DirectoryAsync(request.OrganizationId, ct);
        var path = Path.Combine(directory, request.BackupId.ToString("N") + ".json");
        CheckPath(path); CheckPath(path + ".lock");
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            var existing = await ReadAsync(path, request.OrganizationId, ct);
            if (existing.RepositoryId != request.RepositoryId) throw new InvalidOperationException("Backup job identity belongs to another repository.");
            if (existing.Status != "Failed") return existing;
            var retry = existing with { Status = "Queued", FailureMessage = null, UpdatedAt = DateTimeOffset.UtcNow };
            await WriteAsync(path, retry, ct); return retry;
        }
        if (Directory.EnumerateFiles(directory, "*.json").Take(1000).Count() >= 1000) throw new InvalidOperationException("Backup job history has reached its limit.");
        var now = DateTimeOffset.UtcNow;
        var job = new InternalGitBackupJob(request.BackupId, request.OrganizationId, request.RepositoryId, "Queued", now, now);
        await WriteAsync(path, job, ct); return job;
    }

    public async Task<IReadOnlyList<InternalGitBackupJob>> ListAsync(Guid business, CancellationToken ct = default)
    {
        var directory = await DirectoryAsync(business, ct); var result = new List<InternalGitBackupJob>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(1001))
        {
            if (result.Count == 1000) throw new IOException("Backup job history exceeds its limit.");
            result.Add(await ReadAsync(path, business, ct));
        }
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public async Task ProcessAsync(Guid business, Guid id, CancellationToken ct = default)
    {
        var path = Path.Combine(await DirectoryAsync(business, ct), id.ToString("N") + ".json");
        CheckPath(path); CheckPath(path + ".lock");
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var job = await ReadAsync(path, business, ct);
        if (job.Status is not ("Queued" or "Running")) return;
        job = job with { Status = "Running", UpdatedAt = DateTimeOffset.UtcNow }; await WriteAsync(path, job, ct);
        try
        {
            await store.CreateBackupAsync(new(job.OrganizationId, job.RepositoryId, job.Id), ct);
            await ApplyRetentionAsync(job, ct);
            await WriteAsync(path, job with { Status = "Completed", UpdatedAt = DateTimeOffset.UtcNow }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // A restarted host replays the same durable backup identity.
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException or HttpRequestException or
            Amazon.Runtime.AmazonServiceException or TimeoutException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            await WriteAsync(path, job with { Status = "Failed", UpdatedAt = DateTimeOffset.UtcNow,
                FailureMessage = "Backup did not complete. Check repository and backup storage, then retry this job." }, ct);
        }
    }

    public async Task DismissAsync(Guid business, Guid id, CancellationToken ct = default)
    {
        var path = Path.Combine(await DirectoryAsync(business, ct), id.ToString("N") + ".json");
        CheckPath(path); CheckPath(path + ".lock");
        await using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(path)) return;
        var job = await ReadAsync(path, business, ct);
        if (job.Status is not ("Completed" or "Failed")) throw new InvalidOperationException("Only finished backup jobs can be dismissed.");
        File.Delete(path); // Removes job history only. Completed backup data is managed separately.
    }

    internal async Task ProcessPendingAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Root)) return;
        CheckPath(Root, true);
        foreach (var directory in Directory.EnumerateDirectories(Root))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var business))
            {
                try { await ScheduleDueAsync(business, ct); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) { /* A blocked schedule must not stop existing jobs or other businesses. */ }
                IReadOnlyList<InternalGitBackupJob> pending;
                try { pending = await ListAsync(business, ct); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) { continue; }
                foreach (var job in pending.Where(x => x.Status is "Queued" or "Running").Take(16))
                {
                    try { await ProcessAsync(business, job.Id, ct); }
                    catch (IOException) { /* Another worker or request may hold the job lease. Retry on the next pass. */ }
                }
            }
    }

    private static async Task<InternalGitBackupJob> ReadAsync(string path, Guid business, CancellationToken ct)
    {
        CheckPath(path);
        var job = JsonSerializer.Deserialize<InternalGitBackupJob>(await File.ReadAllTextAsync(path, ct)) ?? throw new IOException("Backup job is invalid.");
        if (job.OrganizationId != business || job.Id.ToString("N") != Path.GetFileNameWithoutExtension(path) || job.RepositoryId == Guid.Empty ||
            job.Status is not ("Queued" or "Running" or "Completed" or "Failed")) throw new IOException("Backup job identity is invalid.");
        return job;
    }

    private static async Task WriteAsync(string path, InternalGitBackupJob job, CancellationToken ct)
    {
        var incoming = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, job, cancellationToken: ct); output.Flush(true); }
            File.Move(incoming, path, true);
        }
        finally { File.Delete(incoming); }
    }
}

public sealed class InternalGitBackupWorker(InternalGitBackupJobs jobs, ILogger<InternalGitBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await jobs.ProcessPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Backup jobs could not be processed; storage will be checked again."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
