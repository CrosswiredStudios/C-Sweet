using System.Net;
using System.Text;
using CSweet.Application.SourceControl;
using CSweet.Infrastructure.SourceControl;

namespace CSweet.UnitTests;

public sealed class GitHubUserAuthorizationClientTests
{
    [Fact]
    public async Task VerifiesAuthenticatedUserCanAccessExactInstallation()
    {
        var handler = new QueueHandler(
            Json("{\"access_token\":\"ghu_secret\",\"token_type\":\"bearer\"}"),
            Json("{\"id\":42,\"login\":\"personal-user\"}"),
            Json("{\"total_count\":1,\"installations\":[{\"id\":99}]}"));
        var client = new GitHubUserAuthorizationClient(new HttpClient(handler));

        var result = await client.VerifyInstallationAsync(
            new PlatformGitHubUserAuthorizationConfiguration("Iv1.client", "client-secret"),
            "one-time-code",
            99);

        Assert.Equal(99, result.InstallationId);
        Assert.Equal(42, result.UserId);
        Assert.Equal("personal-user", result.UserLogin);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Uri.Contains("ghu_secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsInstallationNotVisibleToAuthenticatedUser()
    {
        var handler = new QueueHandler(
            Json("{\"access_token\":\"ghu_secret\",\"token_type\":\"bearer\"}"),
            Json("{\"id\":42,\"login\":\"personal-user\"}"),
            Json("{\"total_count\":1,\"installations\":[{\"id\":100}]}"));
        var client = new GitHubUserAuthorizationClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.VerifyInstallationAsync(
                new PlatformGitHubUserAuthorizationConfiguration("Iv1.client", "client-secret"),
                "one-time-code",
                99));

        Assert.Contains("cannot access", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<(HttpMethod Method, string Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
