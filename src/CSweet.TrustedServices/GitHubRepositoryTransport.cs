using System.Text;

namespace CSweet.TrustedServices;

public interface IGitHubRepositoryTransport
{
    Task DownloadLfsAsync(string cache, GitHubRepositoryDescriptor repository, string token, string sha, string storage, CancellationToken ct) => throw new InvalidOperationException("GitHub LFS download is unavailable.");
    Task UploadLfsAsync(string cache, GitHubRepositoryDescriptor repository, string token, string storage, IReadOnlyList<GitHubLfsObject> objects, CancellationToken ct) => throw new InvalidOperationException("GitHub LFS upload is unavailable.");
    Task<IReadOnlyDictionary<string, string>> RefsAsync(GitHubRepositoryDescriptor repository, string token, CancellationToken ct);
    Task FetchAsync(string cache, GitHubRepositoryDescriptor repository, string token, string sha, CancellationToken ct);
    Task PushAsync(string cache, GitHubRepositoryDescriptor repository, string token, string branch, string sha, string? expected, CancellationToken ct);
}

public sealed class GitHubRepositoryTransport(InternalGitRepositoryStore store) : IGitHubRepositoryTransport
{
    public Task DownloadLfsAsync(string cache, GitHubRepositoryDescriptor repository, string token, string sha, string storage, CancellationToken ct) =>
        store.RunGitHubNetworkAsync(cache, repository, token, [.. LfsConfiguration(repository, storage), "lfs", "fetch", "--include=", "--exclude=", Url(repository), sha], ct);

    public async Task UploadLfsAsync(string cache, GitHubRepositoryDescriptor repository, string token, string storage, IReadOnlyList<GitHubLfsObject> objects, CancellationToken ct)
    {
        foreach (var asset in objects) await GitHubWorkspaceLfs.VerifyAsync(GitHubWorkspaceLfs.ObjectPath(storage, asset.Oid), asset, ct);
        // Bound command length on Windows; all assets must upload before the Git ref is pushed.
        foreach (var batch in objects.DistinctBy(x => x.Oid).Chunk(64))
            await store.RunGitHubNetworkAsync(cache, repository, token,
                [.. LfsConfiguration(repository, storage), "lfs", "push", "--object-id", Url(repository), .. batch.Select(x => x.Oid)], ct);
    }

    private static string[] LfsConfiguration(GitHubRepositoryDescriptor repository, string storage) =>
        ["-c", "lfs.url=" + Url(repository) + "/info/lfs", "-c", "lfs.pushurl=" + Url(repository) + "/info/lfs",
         "-c", "lfs.storage=" + storage, "-c", "lfs.basictransfersonly=true", "-c", "lfs.standalonetransferagent=",
         "-c", "lfs.fetchrecentalways=false", "-c", "lfs.concurrenttransfers=1"];
    public async Task<IReadOnlyDictionary<string, string>> RefsAsync(GitHubRepositoryDescriptor repository, string token, CancellationToken ct)
    {
        var output = await store.RunGitHubNetworkAsync(null, repository, token, ["ls-remote", "--heads", Url(repository)], ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Split('\t', 2)).ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);
    }
    public Task FetchAsync(string cache, GitHubRepositoryDescriptor repository, string token, string sha, CancellationToken ct) =>
        store.RunGitHubNetworkAsync(cache, repository, token, ["fetch", "--no-tags", "--no-recurse-submodules", "--depth=1", Url(repository), sha], ct);
    public Task PushAsync(string cache, GitHubRepositoryDescriptor repository, string token, string branch, string sha, string? expected, CancellationToken ct) =>
        store.RunGitHubNetworkAsync(cache, repository, token, ["push", "--porcelain", "--force-with-lease=refs/heads/" + branch + ":" + (expected ?? ""), Url(repository), sha + ":refs/heads/" + branch], ct);
    private static string Url(GitHubRepositoryDescriptor repository)
    {
        if (!Valid(repository.Owner) || !Valid(repository.Name)) throw new ArgumentException("Invalid GitHub repository coordinates.");
        return $"https://github.com/{repository.Owner}/{repository.Name}.git";
    }
    private static bool Valid(string value) => value.Length is >= 1 and <= 100 && value is not ("." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed partial class InternalGitRepositoryStore
{
    internal async Task<string> RunGitHubNetworkAsync(string? cache, GitHubRepositoryDescriptor repository, string token, string[] args, CancellationToken ct)
    {
        var environment = new Dictionary<string, string> {
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token)) };
        try { return await RunAsync(cache, ["-c", "protocol.https.allow=always", "-c", "http.followRedirects=false", "-c", "credential.helper=", .. args], ct, environment); }
        catch (InvalidOperationException) { throw new InvalidOperationException("GitHub rejected the transfer or its branch changed. Credentials remain confined to GitHost."); }
    }
}
