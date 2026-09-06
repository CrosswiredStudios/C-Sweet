using System.Net;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public class GitHubAgentRepositoryClientTests
{
    [Fact]
    public async Task Client_ResolvesDefaultBranchCommitAndRootManifest()
    {
        var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/repos/example/research-agent" => Json("{\"default_branch\":\"main\"}"),
            "/repos/example/research-agent/commits/main" =>
                Json("{\"sha\":\"0123456789abcdef0123456789abcdef01234567\"}"),
            "/repos/example/research-agent/contents/csweet-plugin.json" =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"manifestVersion\":\"1.0\"}")
                },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var client = CreateClient(handler);

        var branch = await client.GetDefaultBranchAsync("example", "research-agent", CancellationToken.None);
        var sha = await client.ResolveCommitShaAsync("example", "research-agent", branch, CancellationToken.None);
        var manifest = await client.GetRootManifestAsync("example", "research-agent", sha, CancellationToken.None);

        Assert.Equal("main", branch);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", sha);
        Assert.Contains("manifestVersion", Encoding.UTF8.GetString(manifest));
        Assert.Equal(
            "/repos/example/research-agent/contents/csweet-plugin.json?ref=0123456789abcdef0123456789abcdef01234567",
            handler.Requests[2]);
    }

    [Fact]
    public async Task GetRootManifestAsync_RejectsManifestOverOneMegabyte()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(1024 * 1024) + 1])
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            client.GetRootManifestAsync(
                "example",
                "research-agent",
                "0123456789abcdef0123456789abcdef01234567",
                CancellationToken.None));

        Assert.Contains("1 MB", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("catalog-test-token")]
    public async Task Client_UsesConfiguredAuthenticationForEveryRepositoryRequest(string? token)
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            Assert.Equal(token is null ? null : "Bearer", request.Headers.Authorization?.Scheme);
            return request.RequestUri!.AbsolutePath switch
            {
                "/repos/example/agent" => Json("{\"default_branch\":\"main\"}"),
                "/repos/example/agent/commits/main" => Json("{\"sha\":\"0123456789abcdef0123456789abcdef01234567\"}"),
                _ => Json("{}")
            };
        });
        var client = new GitHubAgentRepositoryClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            Options.Create(new GitHubAgentRepositoryOptions { AccessToken = token ?? string.Empty }));
        var branch = await client.GetDefaultBranchAsync("example", "agent", CancellationToken.None);
        var sha = await client.ResolveCommitShaAsync("example", "agent", branch, CancellationToken.None);
        await client.GetRootManifestAsync("example", "agent", sha, CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Client_ExplainsRateLimitAndResetWithoutRawProviderResponse(HttpStatusCode status)
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("provider detail") };
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1788652800");
            return response;
        });
        var error = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            CreateClient(handler).GetDefaultBranchAsync("example", "agent", CancellationToken.None));
        Assert.Contains("rate-limited", error.Message);
        Assert.Contains("Retry after 2026-09-06 00:00:00Z", error.Message);
        Assert.DoesNotContain("provider detail", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Client_PrefersRetryAfterOverReset()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("Retry-After", "90");
            response.Headers.Add("X-RateLimit-Reset", "1788652800");
            return response;
        });
        var error = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            CreateClient(handler).ResolveCommitShaAsync("example", "agent", "main", CancellationToken.None));
        Assert.Contains("Retry after 90 seconds", error.Message);
    }

    [Fact]
    public async Task Client_DoesNotTreatOrdinaryForbiddenAsRateLimit()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("Resource not accessible")
        });
        var error = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            CreateClient(handler).GetDefaultBranchAsync("example", "agent", CancellationToken.None));
        Assert.Contains("403", error.Message);
        Assert.DoesNotContain("rate-limited", error.Message);
    }

    [Fact]
    public async Task Client_ReportsInvalidCredentialWithoutAnonymousRetry()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var error = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            CreateClient(handler).GetDefaultBranchAsync("example", "agent", CancellationToken.None));
        Assert.Contains("credential", error.Message);
        Assert.Single(handler.Requests);
    }

    private static GitHubAgentRepositoryClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") });

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return Task.FromResult(_responseFactory(request));
        }
    }
}
