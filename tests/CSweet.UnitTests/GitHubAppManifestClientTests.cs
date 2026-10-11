using System.Net;
using System.Net.Http.Json;
using CSweet.Infrastructure.SourceControl;

namespace CSweet.UnitTests;

public sealed class GitHubAppManifestClientTests
{
    [Theory]
    [InlineData("octocat", "User")]
    [InlineData("my-company", "Organization")]
    public async Task ConversionReturnsGitHubOwnerIdentity(string login, string type)
    {
        var client = Client(new { login, type });
        var conversion = await client.ConvertAsync("one-time-code");
        Assert.Equal(login, conversion.OwnerLogin);
        Assert.Equal(type, conversion.OwnerType);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "User")]
    [InlineData("octocat", "Unknown")]
    public async Task ConversionRejectsIncompleteOwnerIdentity(string? login, string? type)
    {
        var client = Client(login is null ? null : new { login, type });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConvertAsync("code"));
    }

    private static GitHubAppManifestClient Client(object? owner) => new(new HttpClient(new ConversionApi(owner))
    { BaseAddress = new Uri("https://api.github.com/") });

    private sealed class ConversionApi(object? owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/conversions", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = 42, name = "C-Sweet", slug = "csweet", pem = "test-key", client_id = "client", client_secret = "test-secret", owner })
            });
        }
    }
}
