using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Uses the operator's catalog credential only for GitHub's release asset API.</summary>
public sealed class GitHubReleaseAssetAuthenticationHandler(IGitHubCatalogCredentialProvider credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri is { Scheme: "https", Host: "api.github.com" } uri &&
            uri.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal) && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.Ordinal))
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (await credentials.GetTokenAsync(token) is { } accessToken)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return await base.SendAsync(request, token);
    }
}
