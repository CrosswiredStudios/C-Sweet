using Microsoft.Extensions.Options;

namespace CSweet.TrustedServices;

public sealed partial class InternalGitRepositoryStore
{
    private async Task UploadGitHubLfsAsync(string cache, GitHubRepositoryDescriptor remote, string token, string sha,
        Guid business, Guid repositoryId, WorkspaceArtifactValidator artifacts, IGitHubRepositoryTransport transport, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetFullPath(_options.TemporaryRoot), "github-lfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var archive = Path.Combine(temporary, "source.zip"); var snapshot = Path.Combine(temporary, "source");
            // Keep export attributes from concealing LFS pointers that the published commit references.
            Directory.CreateDirectory(Path.Combine(cache, "info"));
            await File.WriteAllTextAsync(Path.Combine(cache, "info", "attributes"), "* -export-ignore -export-subst\n", ct);
            await RunAsync(cache, ["archive", "--format=zip", "--output=" + archive, sha], ct);
            await using (var input = File.OpenRead(archive)) await artifacts.ExtractZipAsync(input, snapshot, ct);
            var objects = await GitHubWorkspaceLfs.PointersAsync(snapshot, ct);
            if (objects.Count == 0) return;
            var storage = Path.Combine(temporary, "lfs");
            using var local = new InternalGitLfsStore(Options.Create(_options));
            foreach (var asset in objects.DistinctBy(x => x.Oid))
            {
                var path = GitHubWorkspaceLfs.ObjectPath(storage, asset.Oid); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = File.Create(path);
                await local.CopyToAsync(business, repositoryId, asset.Oid, output, ct, asset.Size);
            }
            await transport.UploadLfsAsync(cache, remote, token, storage, objects, ct);
        }
        finally { DeleteOperationDirectory(temporary); }
    }
}
