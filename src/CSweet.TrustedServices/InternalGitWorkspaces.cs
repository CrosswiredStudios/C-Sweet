using System.Globalization;
using Microsoft.Extensions.Options;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<GitHubWorkspaceSnapshot> PrepareAsync(InternalGitWorkspaceRequest request,
        WorkspaceArtifactValidator artifacts, CancellationToken ct = default)
    {
        ValidateBranch(request.DefaultBranch);
        ValidateBranch(request.Branch);
        if (request.WorkspaceId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160)
            throw new ArgumentException("Workspace identity and idempotency key are required.");
        if (request.ExpectedSha is not null) ValidateSha(request.ExpectedSha);
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        if (!Directory.Exists(repository)) throw new KeyNotFoundException("Repository does not exist.");
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var refs = await RefsAsync(repository, ct);
        if (refs.Count == 0 && request.ExpectedSha is null)
        {
            var tree = (await RunAsync(repository, ["mktree"], ct)).Trim();
            var initial = (await RunAsync(repository, ["commit-tree", tree, "-m", "Initialize repository"], ct)).Trim();
            await RunAsync(repository, ["update-ref", $"refs/heads/{request.DefaultBranch}", initial, new string('0', 40)], ct);
            refs = await RefsAsync(repository, ct);
        }
        var resumed = refs.SingleOrDefault(r => r.Name == $"refs/heads/{request.Branch}");
        var sha = request.ExpectedSha ?? resumed?.Sha ?? refs.SingleOrDefault(r => r.Name == $"refs/heads/{request.DefaultBranch}")?.Sha
            ?? throw new InvalidOperationException("Repository default branch has no commit.");
        // Resolve exact commits only; never accept revision expressions from agents.
        var verified = (await RunAsync(repository, ["rev-parse", "--verify", sha + "^{commit}"], ct)).Trim();
        if (!string.Equals(sha, verified, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Source commit mismatch.");
        Directory.CreateDirectory(_options.TemporaryRoot);
        var temporary = Path.Combine(Path.GetFullPath(_options.TemporaryRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var zip = Path.Combine(temporary, "source.zip");
            var sanitized = Path.Combine(temporary, "sanitized");
            // Agent snapshots are complete working copies, not release archives. Do not let export
            // attributes omit tracked files or rewrite their content before the next publication.
            Directory.CreateDirectory(Path.Combine(repository, "info"));
            await File.WriteAllTextAsync(Path.Combine(repository, "info", "attributes"), "* -export-ignore -export-subst\n", ct);
            await RunAsync(repository, ["archive", "--format=zip", "--output=" + zip, sha], ct);
            await using (var input = File.OpenRead(zip)) await artifacts.ExtractZipAsync(input, sanitized, ct);
            using var lfs = new InternalGitLfsStore(Options.Create(_options));
            foreach (var file in Directory.EnumerateFiles(sanitized, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).Length > 1024) continue;
                var text = await File.ReadAllTextAsync(file, ct);
                if (!text.StartsWith("version https://git-lfs.github.com/spec/v1\n", StringComparison.Ordinal) &&
                    !text.StartsWith("version https://git-lfs.github.com/spec/v1\r\n", StringComparison.Ordinal)) continue;
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
                if (lines.Length != 3 || !lines[1].StartsWith("oid sha256:", StringComparison.Ordinal) ||
                    !lines[2].StartsWith("size ", StringComparison.Ordinal) ||
                    !long.TryParse(lines[2][5..], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
                    throw new InvalidDataException("Unsupported or invalid LFS pointer.");
                var asset = Path.Combine(temporary, "asset-" + Guid.NewGuid().ToString("N"));
                await using (var target = File.Create(asset))
                {
                    await lfs.CopyToAsync(request.OrganizationId, request.RepositoryId, lines[1][11..], target, ct);
                    if (target.Length != size) throw new InvalidDataException("LFS object size differs from its pointer.");
                }
                File.Move(asset, file, overwrite: true);
            }
            await using var output = new MemoryStream();
            var manifest = await artifacts.CreateZipAsync(sanitized, output, ct);
            return new($"workspace-{request.WorkspaceId:N}", sha, resumed is not null, output.ToArray(), manifest);
        }
        finally
        {
            var root = Path.GetFullPath(_options.TemporaryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(temporary).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid temporary path.");
            Directory.Delete(temporary, recursive: true);
        }
    }
}
