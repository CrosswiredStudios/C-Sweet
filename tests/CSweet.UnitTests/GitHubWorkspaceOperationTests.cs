using System.Diagnostics;
using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class GitHubWorkspaceOperationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "csweet-github-tests", Guid.NewGuid().ToString("N"));
    private readonly Guid business = Guid.NewGuid(), repository = Guid.NewGuid(), workspace = Guid.NewGuid();
    private readonly WorkspaceArtifactValidator artifacts = new();
    private readonly InternalGitRepositoryStore store;
    private readonly LocalTransport transport;
    private static readonly GitHubRepositoryDescriptor Remote = new(42, "owner", "repo", "owner/repo", "https://github.com/owner/repo.git", "main", true, false, false);
    public GitHubWorkspaceOperationTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".csweet-git-store"), "tests");
        Directory.CreateDirectory(Path.Combine(root, "lfs")); File.WriteAllText(Path.Combine(root, "lfs", ".csweet-object-store"), "tests");
        store = new(Options.Create(new InternalGitStorageOptions { RepositoryRoot = root, ExpectedStoreId = "tests", TemporaryRoot = Path.Combine(root, "temp"),
            Lfs = new() { RootPath = Path.Combine(root, "lfs"), ExpectedStoreId = "tests" } }));
        transport = new(Path.Combine(root, "remote.git"));
    }
    private async Task<GitHubSnapshotOperation> RequestAsync(params (string Name, string Content)[] files)
    {
        await Git(null, "init", "--bare", "--template=", "--initial-branch=main", transport.Path);
        var tree = (await Git(transport.Path, "mktree")).Trim();
        var sha = (await Git(transport.Path, "commit-tree", tree, "-m", "Initial")).Trim();
        await Git(transport.Path, "update-ref", "refs/heads/main", sha);
        var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        foreach (var file in files) await File.WriteAllTextAsync(Path.Combine(input, file.Name), file.Content);
        using var output = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, output);
        return new(12, 42, "owner", "repo", new(business, repository, workspace, "publish", sha, "work/one", "main", "once",
            output.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, "Implement feature"), "Feature");
    }
    private Task<InternalGitSnapshotResult> Apply(GitHubSnapshotOperation request) => store.ApplyGitHubSnapshotAsync(request, Remote, "secret", artifacts, transport);

    [Fact]
    public async Task LostPushResponseReplaysExactCommitWithoutChangingMain()
    {
        var request = await RequestAsync(("code.txt", "feature")); transport.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(() => Apply(request));
        var head = (await Git(transport.Path, "rev-parse", "refs/heads/work/one")).Trim();
        var result = await Apply(request);
        Assert.Equal(head, result.CommitSha); Assert.Equal(1, transport.Pushes);
        Assert.Equal(request.Workspace.BaseSha, (await Git(transport.Path, "rev-parse", "main")).Trim());
        Assert.Equal("feature", await Git(transport.Path, "show", head + ":code.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(request with { ProposedChangeTitle = "Different request" }));
    }

    [Fact]
    public async Task ConcurrentBranchCreationCannotBeOverwrittenAndRetryFailsClosed()
    {
        var request = await RequestAsync(("code.txt", "feature")); transport.RaceSha = request.Workspace.BaseSha;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(request));
        Assert.Equal(request.Workspace.BaseSha, (await Git(transport.Path, "rev-parse", "refs/heads/work/one")).Trim());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(request)); Assert.Equal(1, transport.Pushes);
    }

    [Fact]
    public async Task DeletedBranchAfterLostResponseIsNotSilentlyRecreated()
    {
        var request = await RequestAsync(("code.txt", "feature")); transport.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(() => Apply(request));
        await Git(transport.Path, "update-ref", "-d", "refs/heads/work/one");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(request)); Assert.Equal(1, transport.Pushes);
    }

    [Fact]
    public async Task UnresolvedLfsPointersCannotBePublished()
    {
        var request = await RequestAsync(("asset.bin", "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 10\n"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(request)); Assert.Equal(0, transport.Pushes);
    }

    [Fact]
    public async Task LfsUploadFailureLeavesRemoteUnchangedAndRetryMaterializesVerifiedAsset()
    {
        var request = await RequestAsync((".gitattributes", "*.bin filter=lfs diff=lfs merge=lfs -text\n"), ("asset.bin", "original asset"));
        transport.FailLfs = true;
        await Assert.ThrowsAsync<IOException>(() => Apply(request)); Assert.Equal(0, transport.Pushes);
        Assert.Equal(request.Workspace.BaseSha, (await Git(transport.Path, "rev-parse", "main")).Trim());
        transport.FailLfs = false;
        var result = await Apply(request); Assert.Equal(1, transport.Pushes);
        Assert.Equal(result.CommitSha, (await Apply(request)).CommitSha); Assert.Equal(1, transport.Pushes);
        var pointer = await Git(transport.Path, "show", result.CommitSha + ":asset.bin");
        Assert.StartsWith("version https://git-lfs.github.com/spec/v1", pointer);
        var directory = System.IO.Path.Combine(root, "materialized"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "asset.bin"), pointer);
        await GitHubWorkspaceLfs.MaterializeAsync(directory, System.IO.Path.Combine(root, "cache"), Remote, "secret", result.CommitSha!, transport, default);
        Assert.Equal("original asset", await File.ReadAllTextAsync(System.IO.Path.Combine(directory, "asset.bin")));
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "asset.bin"), pointer);
        transport.CorruptLfs = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubWorkspaceLfs.MaterializeAsync(directory, System.IO.Path.Combine(root, "cache"), Remote, "secret", result.CommitSha!, transport, default));
        Assert.Equal(pointer, await File.ReadAllTextAsync(System.IO.Path.Combine(directory, "asset.bin")));
    }

    [Fact]
    public async Task InvalidManifestCannotReachRemotePush()
    {
        var request = await RequestAsync(("code.txt", "feature"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Apply(request with { Workspace = request.Workspace with { ArchiveManifestSha = new string('a', 64) } }));
        Assert.Equal(0, transport.Pushes);
    }

    private sealed class LocalTransport(string path) : IGitHubRepositoryTransport
    {
        public string Path => path;
        public bool LoseResponse; public string? RaceSha; public int Pushes;
        public bool FailLfs, CorruptLfs;
        private readonly Dictionary<string, byte[]> assets = [];
        public async Task UploadLfsAsync(string cache, GitHubRepositoryDescriptor repo, string token, string storage, IReadOnlyList<GitHubLfsObject> objects, CancellationToken ct)
        {
            if (FailLfs) throw new IOException("Simulated LFS failure");
            foreach (var asset in objects)
            {
                var file = GitHubWorkspaceLfs.ObjectPath(storage, asset.Oid);
                await GitHubWorkspaceLfs.VerifyAsync(file, asset, ct);
                assets[asset.Oid] = await File.ReadAllBytesAsync(file, ct);
            }
        }
        public async Task DownloadLfsAsync(string cache, GitHubRepositoryDescriptor repo, string token, string sha, string storage, CancellationToken ct)
        {
            foreach (var asset in assets)
            {
                var file = GitHubWorkspaceLfs.ObjectPath(storage, asset.Key); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                var bytes = asset.Value.ToArray(); if (CorruptLfs) bytes[0] ^= 1;
                await File.WriteAllBytesAsync(file, bytes, ct);
            }
        }
        public async Task<IReadOnlyDictionary<string, string>> RefsAsync(GitHubRepositoryDescriptor repo, string token, CancellationToken ct) =>
            (await Git(path, "for-each-ref", "--format=%(objectname) %(refname)", "refs/heads/")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Split(' ', 2)).ToDictionary(parts => parts[1], parts => parts[0]);
        public Task FetchAsync(string cache, GitHubRepositoryDescriptor repo, string token, string sha, CancellationToken ct) =>
            Git(cache, "fetch", "--depth=1", "--no-tags", path, sha);
        public async Task PushAsync(string cache, GitHubRepositoryDescriptor repo, string token, string branch, string sha, string? expected, CancellationToken ct)
        {
            Pushes++;
            if (RaceSha is not null) await Git(path, "update-ref", "refs/heads/" + branch, RaceSha);
            await Git(cache, "push", "--force-with-lease=refs/heads/" + branch + ":" + (expected ?? ""), path, sha + ":refs/heads/" + branch);
            if (LoseResponse) { LoseResponse = false; throw new IOException("Simulated lost response"); }
        }
    }
    private static async Task<string> Git(string? repository, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var arg in new[] { "-c", "protocol.file.allow=always", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "user.name=Tests", "-c", "user.email=test@example.invalid" }) start.ArgumentList.Add(arg);
        if (repository is not null) { start.ArgumentList.Add("--git-dir"); start.ArgumentList.Add(repository); }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        await error; return await output;
    }
    public void Dispose()
    {
        if (Directory.Exists(root)) { foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true); }
    }
}
