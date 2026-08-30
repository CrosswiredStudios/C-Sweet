namespace CSweet.Application.Setup;

public interface IGitHubAgentRepositoryClient
{
    Task<string> GetDefaultBranchAsync(
        string repositoryOwner,
        string repositoryName,
        CancellationToken cancellationToken);

    Task<string> ResolveCommitShaAsync(
        string repositoryOwner,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken);

    Task<byte[]> GetRootManifestAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        CancellationToken cancellationToken);

    Task<byte[]?> GetRepositoryFileAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    async Task<PluginManifestSource> GetRootPluginManifestAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        CancellationToken cancellationToken) =>
        new("csweet-plugin.json", await GetRootManifestAsync(
            repositoryOwner, repositoryName, commitSha, cancellationToken));
}

public sealed record PluginManifestSource(string FileName, byte[] Content);
