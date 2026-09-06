using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.SourceControl;
using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<InternalGitSnapshotResult> ApplySnapshotAsync(InternalGitSnapshotOperation request,
        WorkspaceArtifactValidator artifacts, CancellationToken ct = default)
    {
        ValidateSha(request.BaseSha); ValidateBranch(request.Branch); ValidateBranch(request.DefaultBranch);
        if (request.Operation is not ("inspect" or "publish" or "refresh") || request.WorkspaceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160 ||
            request.Archive.Length > 600L * 1024 * 1024)
            throw new ArgumentException("Invalid workspace operation.");
        if (request.Operation == "publish" && (string.IsNullOrWhiteSpace(request.CommitMessage) || request.CommitMessage.Length > 512 || request.CommitMessage.Contains('\0')))
            throw new ArgumentException("A bounded commit message is required.");
        if (request.Branch == request.DefaultBranch && request.Operation == "publish")
            throw new UnauthorizedAccessException("Publication cannot write directly to the default branch.");
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.WorkspaceId:N}:{request.IdempotencyKey}"))).ToLowerInvariant();
        var receiptRef = "refs/csweet/publications/" + identity;
        var refs = await RefsAsync(repository, ct);
        var source = refs.SingleOrDefault(r => r.Name == "refs/heads/" + request.Branch)?.Sha;
        var target = refs.SingleOrDefault(r => r.Name == "refs/heads/" + request.DefaultBranch)?.Sha;
        Directory.CreateDirectory(_options.TemporaryRoot);
        var temporary = Path.Combine(Path.GetFullPath(_options.TemporaryRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var extracted = Path.Combine(temporary, "snapshot");
            await using var archive = new MemoryStream(request.Archive, writable: false);
            var manifest = await artifacts.ExtractZipAsync(archive, extracted, ct);
            if (manifest != new WorkspaceArtifactManifest(request.ArchiveManifestSha, request.FileCount, request.TotalBytes))
                throw new InvalidDataException("Workspace snapshot differs from its broker manifest.");
            var tree = await WriteSnapshotTreeAsync(repository, extracted, temporary, request.BaseSha, request.OrganizationId, request.RepositoryId, ct, request.AllowLfs);
            var changed = (await RunAsync(repository, ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", request.BaseSha, tree, "--"], ct))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var summary = await RunAsync(repository, ["diff", "--no-ext-diff", "--no-textconv", "--stat", request.BaseSha, tree, "--"], ct);
            if (request.Operation != "publish")
                return new(changed.Length == 0 ? "Clean" : "Modified", request.BaseSha, null, changed, summary, source ?? target);

            // Ref transactions persist the receipt and publication together, surviving a lost HTTP response.
            var receipt = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", receiptRef], ct)).Trim();
            if (receipt.Length > 0)
            {
                if (source != receipt) throw new InvalidOperationException("This publication was superseded by a later work-branch revision.");
                var oldTree = (await RunAsync(repository, ["rev-parse", receipt + "^{tree}"], ct)).Trim();
                var oldMessage = (await RunAsync(repository, ["log", "-1", "--format=%B", receipt], ct)).TrimEnd();
                if (tree != oldTree || oldMessage != request.CommitMessage!.TrimEnd())
                    throw new InvalidOperationException("The idempotency key was already used with different content.");
                return new("Published", request.BaseSha, receipt, changed, summary, target);
            }
            if (source is not null && source != request.BaseSha)
                throw new InvalidOperationException("The work branch changed. Prepare or refresh its exact current revision before publishing.");
            if (await FindLockedChangeAsync(repository, request.BaseSha, tree, ct) is { } lockedPath)
                return new("Locked", request.BaseSha, null, changed, $"Publication changes locked file {lockedPath}. Ask its owner to release the lock before publishing.", target);
            var commit = (await RunAsync(repository, ["commit-tree", tree, "-p", request.BaseSha, "-m", request.CommitMessage!], ct)).Trim();
            var transaction = $"start\nupdate refs/heads/{request.Branch} {commit} {source ?? new string('0', 40)}\ncreate {receiptRef} {commit}\nprepare\ncommit\n";
            await RunAsync(repository, ["update-ref", "--stdin"], ct, input: transaction);
            return new("Published", request.BaseSha, commit, changed, summary, target);
        }
        finally { DeleteOperationDirectory(temporary); }
    }

    private async Task<string> WriteSnapshotTreeAsync(string repository, string directory, string temporary,
        string baseSha, Guid business, Guid repositoryId, CancellationToken ct, bool allowLfs = true)
    {
        if (!allowLfs) await GitHubWorkspaceContent.ValidateAsync(directory, ct);
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(temporary, "index") };
        await RunAsync(repository, ["read-tree", "--empty"], ct, environment);
        var entries = (await RunAsync(repository, ["ls-tree", "-r", "-z", baseSha], ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var modes = entries.Select(e => e.Split('\t', 2)).ToDictionary(e => e[1], e => e[0].Split(' ')[0], StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path).Replace('\\', '/'))).ToList();
        // Attribute lookup uses only this snapshot's declarative attributes. Git never runs a clean filter.
        foreach (var file in files.Where(f => Path.GetFileName(f.Path) == ".gitattributes")) await AddAsync(file.Path, file.Relative);
        var attributes = files.Count == 0 ? [] : (await RunAsync(repository,
            ["check-attr", "--cached", "-z", "--stdin", "filter"], ct, environment,
            string.Join('\0', files.Select(f => f.Relative)) + "\0")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var lfsPaths = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < attributes.Length; i += 3) if (attributes[i + 2] == "lfs") lfsPaths.Add(attributes[i]);
        if (!allowLfs && lfsPaths.Count > 0) throw new InvalidOperationException("GitHub LFS publication is not yet supported. No remote branch was changed.");
        using var lfs = new InternalGitLfsStore(Options.Create(_options));
        foreach (var file in files)
        {
            var content = file.Path;
            if (lfsPaths.Contains(file.Relative))
            {
                await using var stream = File.OpenRead(file.Path);
                var oid = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
                stream.Position = 0;
                await lfs.PutAsync(business, repositoryId, oid, stream.Length, stream, ct);
                content = Path.Combine(temporary, "pointer-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllTextAsync(content, $"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize {stream.Length}\n", new UTF8Encoding(false), ct);
            }
            await AddAsync(content, file.Relative);
        }
        // Gitlinks have no file content in the snapshot; retain them rather than silently deleting submodules.
        foreach (var entry in entries.Where(e => e.StartsWith("160000 ", StringComparison.Ordinal)))
        {
            var parts = entry.Split('\t', 2); var metadata = parts[0].Split(' ');
            await RunAsync(repository, ["update-index", "-z", "--index-info"], ct, environment, $"160000 {metadata[2]}\t{parts[1]}\0");
        }
        return (await RunAsync(repository, ["write-tree"], ct, environment)).Trim();

        async Task AddAsync(string content, string relative)
        {
            var sha = (await RunAsync(repository, ["hash-object", "-w", "--no-filters", "--", content], ct)).Trim();
            var mode = modes.GetValueOrDefault(relative) == "100755" ? "100755" : "100644";
            await RunAsync(repository, ["update-index", "-z", "--index-info"], ct, environment, $"{mode} {sha}\t{relative}\0");
        }
    }

    public async Task<InternalGitMergeResult> MergeInternalAsync(InternalGitMergeRequest request, CancellationToken ct = default)
    {
        ValidateBranch(request.SourceBranch); ValidateBranch(request.TargetBranch); ValidateSha(request.ExpectedHeadSha);
        if (request.SourceBranch == request.TargetBranch || request.PublicationId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("Merge identity is required.");
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)))).ToLowerInvariant();
        var message = $"Merge C-Sweet publication {request.PublicationId:D}\n\nC-Sweet-Merge-Identity: {identity}";
        var receiptRef = "refs/csweet/merges/" + request.PublicationId.ToString("N");
        var receipt = (await RunAsync(repository, ["for-each-ref", "--format=%(objectname)", receiptRef], ct)).Trim();
        if (receipt.Length > 0)
        {
            var parents = (await RunAsync(repository, ["show", "-s", "--format=%P", receipt], ct)).Trim().Split(' ');
            var receiptMessage = (await RunAsync(repository, ["log", "-1", "--format=%B", receipt], ct)).TrimEnd();
            if (parents.Length != 2 || parents[^1] != request.ExpectedHeadSha || receiptMessage != message)
                throw new InvalidOperationException("Publication merge receipt belongs to a different merge request.");
            return new(true, true, receipt);
        }
        var refs = await RefsAsync(repository, ct);
        var source = refs.SingleOrDefault(r => r.Name == "refs/heads/" + request.SourceBranch)?.Sha;
        var target = refs.SingleOrDefault(r => r.Name == "refs/heads/" + request.TargetBranch)?.Sha;
        if (source != request.ExpectedHeadSha) return new(false, false, null, "head_changed", "The proposed-change head changed after authorization.");
        if (target is null) return new(false, true, null, "target_missing", "The target branch does not exist.");
        string tree;
        try { tree = (await RunAsync(repository, ["merge-tree", "--write-tree", target, source], ct)).Split('\n')[0].Trim(); }
        catch (InvalidOperationException) { return new(false, true, null, "merge_conflict", "Resolve conflicts and obtain validation for the updated proposal."); }
        ValidateSha(tree);
        if (await FindLockedChangeAsync(repository, target, tree, ct) is { } lockedPath)
            return new(false, true, null, "file_locked", $"Merge changes locked file {lockedPath}. Release the lock before retrying.");
        var commit = (await RunAsync(repository, ["commit-tree", tree, "-p", target, "-p", source,
            "-m", message], ct)).Trim();
        await RunAsync(repository, ["update-ref", "--stdin"], ct, input:
            $"start\nverify refs/heads/{request.SourceBranch} {source}\nupdate refs/heads/{request.TargetBranch} {commit} {target}\ncreate {receiptRef} {commit}\nprepare\ncommit\n");
        return new(true, true, commit);
    }

    private void DeleteOperationDirectory(string directory)
    {
        var prefix = Path.GetFullPath(_options.TemporaryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Invalid temporary path.");
        Directory.Delete(directory, true);
    }
}
