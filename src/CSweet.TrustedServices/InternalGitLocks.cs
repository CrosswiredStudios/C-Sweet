using System.Text.Json;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<InternalGitLockResult> LocksAsync(InternalGitLockRequest request, CancellationToken ct = default)
    {
        if (request.ActorId == Guid.Empty || request.Operation is not ("list" or "verify" or "create" or "unlock") || request.Limit is < 1 or > 1000)
            throw new ArgumentException("Invalid lock operation.");
        if (!string.IsNullOrEmpty(request.Path)) ValidateLockPath(request.Path);
        if (!string.IsNullOrEmpty(request.Id) && !Guid.TryParseExact(request.Id, "N", out _)) throw new ArgumentException("Invalid lock ID.");
        if (!string.IsNullOrEmpty(request.Cursor) && !Guid.TryParseExact(request.Cursor, "N", out _)) throw new ArgumentException("Invalid lock cursor.");
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var file = Path.Combine(repository, "csweet-lfs-locks.json");
        var locks = await ReadFileLocksAsync(repository, ct);
        if (request.Operation is "list" or "verify")
        {
            var matching = locks.Where(l => (string.IsNullOrEmpty(request.Path) || l.Path == request.Path) &&
                (string.IsNullOrEmpty(request.Id) || l.Id == request.Id) &&
                (string.IsNullOrEmpty(request.Cursor) || string.CompareOrdinal(l.Id, request.Cursor) > 0))
                .OrderBy(l => l.Id, StringComparer.Ordinal).Take(request.Limit + 1).ToList();
            var page = matching.Take(request.Limit).ToList();
            return new(200, page, matching.Count > request.Limit ? page[^1].Id : null);
        }
        InternalGitFileLock selected;
        if (request.Operation == "create")
        {
            ValidateLockPath(request.Path);
            var existing = locks.SingleOrDefault(l => l.Path == request.Path);
            if (existing is not null) return new(409, [existing], Message: "This path is already locked.");
            if (locks.Count >= 10000) throw new InvalidOperationException("Repository lock limit reached.");
            if (string.IsNullOrWhiteSpace(request.ActorName) || request.ActorName.Length > 256) throw new ArgumentException("Lock owner name is required.");
            selected = new(Guid.NewGuid().ToString("N"), request.Path!, request.ActorId, request.ActorName, DateTimeOffset.UtcNow);
            locks.Add(selected);
        }
        else
        {
            var existing = locks.SingleOrDefault(l => l.Id == request.Id);
            if (existing is null) return new(404, [], Message: "Lock not found.");
            if (existing.OwnerId != request.ActorId && !(request.Force && request.CanForce)) return new(403, [], Message: "Only the owner can unlock this file. Managers may explicitly force unlock.");
            selected = existing; locks.Remove(existing);
        }
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, locks, cancellationToken: ct);
                if (output.Length > 16 * 1024 * 1024) throw new IOException("Lock storage exceeds its size limit.");
                output.Flush(true); }
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return new(request.Operation == "create" ? 201 : 200, [selected]);
    }
    // Call while holding the repository lease so lock ownership cannot change before a ref write.
    private static async Task<List<InternalGitFileLock>> ReadFileLocksAsync(string repository, CancellationToken ct)
    {
        var file = Path.Combine(repository, "csweet-lfs-locks.json");
        if (new FileInfo(file).LinkTarget is not null) throw new IOException("Lock storage cannot be a symbolic link.");
        if (!File.Exists(file)) return [];
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > 16 * 1024 * 1024)
            throw new IOException("Lock storage is invalid or exceeds its size limit.");
        var locks = JsonSerializer.Deserialize<List<InternalGitFileLock>>(await File.ReadAllTextAsync(file, ct)) ?? throw new InvalidDataException("Lock storage is invalid.");
        if (locks.Count > 10000 || locks.Any(l => l is null || l.OwnerId == Guid.Empty || !Guid.TryParseExact(l.Id, "N", out _)) ||
            locks.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != locks.Count || locks.Select(l => l.Path).Distinct(StringComparer.Ordinal).Count() != locks.Count)
            throw new InvalidDataException("Lock storage contains invalid ownership records.");
        foreach (var item in locks) ValidateLockPath(item.Path);
        return locks;
    }
    private async Task EnsureRefUnlockedAsync(string repository, string before, string after, CancellationToken ct)
    {
        if ((await ReadFileLocksAsync(repository, ct)).Count == 0) return;
        var zero = new string('0', 40);
        if (before == zero)
        {
            try { before = (await RunAsync(repository, ["merge-base", "HEAD", after], ct)).Trim(); }
            catch (InvalidOperationException) { before = (await RunAsync(repository, ["hash-object", "-w", "-t", "tree", "--stdin"], ct, input: "")).Trim(); }
        }
        if (after == zero) after = (await RunAsync(repository, ["hash-object", "-w", "-t", "tree", "--stdin"], ct, input: "")).Trim();
        if (await FindLockedChangeAsync(repository, before, after, ct) is { } lockedPath)
            throw new InvalidOperationException($"Ref change affects locked file {lockedPath}. Release the lock before retrying.");
    }
    private async Task<string?> FindLockedChangeAsync(string repository, string before, string after, CancellationToken ct)
    {
        var locks = await ReadFileLocksAsync(repository, ct);
        if (locks.Count == 0) return null;
        var changed = (await RunAsync(repository, ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", before, after, "--"], ct))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return locks.FirstOrDefault(l => changed.Contains(l.Path))?.Path;
    }
    private static void ValidateLockPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Lock paths must be repository-relative file paths.");
    }
}
