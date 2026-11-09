using System.Net;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class GitHubReleaseAssetAuthenticationTests
{
    [Theory]
    [InlineData("https://api.github.com/repos/example/provider/releases/assets/42", true)]
    [InlineData("https://github.com/example/provider/releases/download/v1/bundle.cspb", false)]
    [InlineData("https://api.github.com.attacker.test/repos/example/provider/releases/assets/42", false)]
    [InlineData("http://api.github.com/repos/example/provider/releases/assets/42", false)]
    [InlineData("https://downloads.example.test/bundle.cspb", false)]
    public async Task Private_assets_receive_catalog_authentication_only_on_GitHub_asset_api(string url, bool authenticate)
    {
        var terminal = new Terminal(request => {
            Assert.Equal(authenticate ? "synthetic-catalog-read-token" : null, request.Headers.Authorization?.Parameter);
            Assert.Equal(authenticate, request.Headers.Accept.Any(x => x.MediaType == "application/octet-stream"));
        });
        using var handler = new GitHubReleaseAssetAuthenticationHandler(Options.Create(new GitHubAgentRepositoryOptions {
            AccessToken = "synthetic-catalog-read-token" })) { InnerHandler = terminal };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private sealed class Terminal(Action<HttpRequestMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { inspect(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
