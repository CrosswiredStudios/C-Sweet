using System.Security.Cryptography;
using CSweet.Contracts.SourceControl;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    public async Task<InternalRepositoryImportResult> ImportAsync(InternalRepositoryImportRequest request,
        Stream archive, WorkspaceArtifactValidator validator, CancellationToken token)
    {
        ValidateBranch(request.DefaultBranch);
        if (request.ArchiveSha256.Length != 64 || request.ArchiveSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A content-addressed archive is required.");
        VerifyRoot();
        using var businessLease = EnterBusinessStorage(request.OrganizationId);
        var repository = RepositoryPath(request.OrganizationId, request.RepositoryId);
        Directory.CreateDirectory(Path.GetDirectoryName(repository)!);
        await using var lease = new FileStream(repository + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Directory.Exists(repository))
            await RunAsync(null, ["init", "--bare", "--template=", "--initial-branch=" + request.DefaultBranch, repository], token);
        var receipt = "refs/csweet/imports/" + request.ArchiveSha256.ToLowerInvariant();
        var receipts = await RunAsync(repository, ["for-each-ref", "--format=%(refname) %(objectname)", receipt], token);
        var recorded = receipts.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.StartsWith(receipt + " ", StringComparison.Ordinal));
        if (recorded is not null)
        {
            var sha = recorded[(receipt.Length + 1)..].Trim(); ValidateSha(sha);
            return new(sha, true);
        }
        var references = await RefsAsync(repository, token);
        if (references.Count > 0) throw new InvalidOperationException("An import cannot replace an existing repository.");
        var temp = Path.Combine(BusinessTemporaryRoot(request.OrganizationId), "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, "import.zip");
            await using (var output = File.Create(file))
            {
                var buffer = new byte[81920];long total = 0;int read;
                while ((read = await archive.ReadAsync(buffer, token)) > 0)
                {
                    total += read;if (total > 512L * 1024 * 1024) throw new InvalidDataException("The import archive is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            await using (var input = File.OpenRead(file))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
                if (hash != request.ArchiveSha256.ToLowerInvariant()) throw new InvalidDataException("The import archive changed during transfer.");
                input.Position = 0;
                await validator.ExtractZipAsync(input, Path.Combine(temp, "files"), token);
            }
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(temp, "index"), ["GIT_AUTHOR_NAME"] = "C-Sweet",
                ["GIT_AUTHOR_EMAIL"] = "import@csweet.local", ["GIT_COMMITTER_NAME"] = "C-Sweet", ["GIT_COMMITTER_EMAIL"] = "import@csweet.local"
            };
            await RunAsync(repository, ["read-tree", "--empty"], token, environment);
            var root = Path.Combine(temp, "files");
            var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var inputPaths = string.Join("\n", paths.Select(path => "\"" + path.Replace('\\', '/').Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("\b", "\\b").Replace("\f", "\\f")
                .Replace("\v", "\\v").Replace("\a", "\\a") + "\"")) + "\n";
            var hashes = (await RunAsync(repository, ["hash-object", "-w", "--no-filters", "--stdin-paths"], token, environment, inputPaths))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
            if (hashes.Length != paths.Length) throw new InvalidDataException("The imported file set could not be hashed completely.");
            var index = new System.Text.StringBuilder();
            for (var i = 0; i < paths.Length; i++)
            {
                var relative = Path.GetRelativePath(root, paths[i]).Replace('\\', '/');
                var blob = hashes[i];
                ValidateSha(blob);
                index.Append("100644 ").Append(blob).Append('\t').Append(relative).Append('\0');
            }
            await RunAsync(repository, ["update-index", "-z", "--index-info"], token, environment, index.ToString());
            var tree = (await RunAsync(repository, ["write-tree"], token, environment)).Trim();ValidateSha(tree);
            var commit = (await RunAsync(repository, ["commit-tree", tree, "-m", "Import reviewed repository archive"], token, environment)).Trim();ValidateSha(commit);
            await RunAsync(repository, ["update-ref", "--stdin"], token, environment,
                $"start\ncreate refs/heads/{request.DefaultBranch} {commit}\ncreate {receipt} {commit}\nprepare\ncommit\n");
            return new(commit, false);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
}
