using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CSweet.Application.Setup;

namespace CSweet.Infrastructure.Setup;

public sealed class GitHubAgentRepositoryClient : IGitHubAgentRepositoryClient, IPluginSourceResolver
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private readonly HttpClient _httpClient;

    public GitHubAgentRepositoryClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetDefaultBranchAsync(
        string repositoryOwner,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"repos/{Escape(repositoryOwner)}/{Escape(repositoryName)}",
            cancellationToken);
        await EnsureSuccessAsync(response, "Repository was not found or is not public.", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("default_branch", out var branchElement) ||
            string.IsNullOrWhiteSpace(branchElement.GetString()))
        {
            throw new AgentImportPreviewException("GitHub did not return a default branch.");
        }

        return branchElement.GetString()!;
    }

    public async Task<string> ResolveCommitShaAsync(
        string repositoryOwner,
        string repositoryName,
        string reference,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"repos/{Escape(repositoryOwner)}/{Escape(repositoryName)}/commits/{Escape(reference)}",
            cancellationToken);
        await EnsureSuccessAsync(response, "The requested Git reference could not be resolved.", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("sha", out var shaElement) ||
            shaElement.GetString() is not { Length: 40 } commitSha ||
            !commitSha.All(Uri.IsHexDigit))
        {
            throw new AgentImportPreviewException("GitHub returned an invalid commit SHA.");
        }

        return commitSha.ToLowerInvariant();
    }

    async Task<string> IPluginSourceResolver.ResolveCommitShaAsync(
        string repositoryUrl,
        string reference,
        CancellationToken cancellationToken)
    {
        var repository = GitHubRepositoryUrlNormalizer.Normalize(repositoryUrl);
        return await ResolveCommitShaAsync(repository.Owner, repository.Name, reference, cancellationToken);
    }

    public async Task<byte[]> GetRootManifestAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        CancellationToken cancellationToken)
    {
        var manifest = await GetManifestAsync(repositoryOwner, repositoryName, commitSha, "csweet-plugin.json", cancellationToken);
        return manifest ?? throw new AgentImportPreviewException(
            "The repository does not contain a root csweet-plugin.json at the resolved commit.");
    }

    public async Task<PluginManifestSource> GetRootPluginManifestAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        CancellationToken cancellationToken)
    {
        return new("csweet-plugin.json", await GetRootManifestAsync(
            repositoryOwner, repositoryName, commitSha, cancellationToken));
    }

    public Task<byte[]?> GetRepositoryFileAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > MaximumManifestBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || segments.Contains("..", StringComparer.Ordinal))
            throw new AgentImportPreviewException("Repository resource path is invalid.");
        return GetFileAsync(repositoryOwner, repositoryName, commitSha, normalized, maximumBytes, cancellationToken);
    }

    private async Task<byte[]?> GetManifestAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        string fileName,
        CancellationToken cancellationToken)
    {
        return await GetFileAsync(repositoryOwner, repositoryName, commitSha, fileName, MaximumManifestBytes, cancellationToken);
    }

    private async Task<byte[]?> GetFileAsync(
        string repositoryOwner,
        string repositoryName,
        string commitSha,
        string relativePath,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{Escape(repositoryOwner)}/{Escape(repositoryName)}/contents/{string.Join('/', relativePath.Split('/').Select(Escape))}?ref={Escape(commitSha)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, $"The repository resource '{relativePath}' could not be read at the resolved commit.", cancellationToken);

        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new AgentImportPreviewException("Plugin manifest exceeds the 1 MB preview limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            if (output.Length + count > maximumBytes)
            {
                throw new AgentImportPreviewException("Plugin manifest exceeds the 1 MB preview limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        return output.ToArray();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string notFoundMessage,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AgentImportPreviewException(notFoundMessage);
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AgentImportPreviewException(
            $"GitHub request failed with status {(int)response.StatusCode}: {detail}");
    }
}
