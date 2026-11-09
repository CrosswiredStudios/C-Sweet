using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Uses the operator's catalog credential only for GitHub's release asset API.</summary>
public sealed class GitHubReleaseAssetAuthenticationHandler(IOptions<GitHubAgentRepositoryOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri is { Scheme: "https", Host: "api.github.com" } uri &&
            uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal) && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.Ordinal))
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (!string.IsNullOrWhiteSpace(options.Value.AccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken.Trim());
        }
        return base.SendAsync(request, token);
    }
}
