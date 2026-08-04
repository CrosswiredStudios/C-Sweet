using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class GitHubAppClientTests
{
    [Fact]
    public async Task ExactShaMergeStopsBeforeMutationWhenHeadChanged()
    {
        var handler = new SequenceHandler(
            Json(HttpStatusCode.Created, "{\"token\":\"installation-secret\"}"),
            Json(HttpStatusCode.OK, $"{{\"head\":{{\"sha\":\"{new string('b', 40)}\"}}}}"));
        var client = CreateClient(handler);

        var result = await client.MergePullRequestAsync(
            new GitHubMergeRequest(
                12, "approved-org", "private-project", 4,
                new string('a', 40), "merge-once"),
            CancellationToken.None);

        Assert.False(result.Merged);
        Assert.False(result.HeadMatched);
        Assert.Equal("head_changed", result.FailureCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request =>
            request.Path.EndsWith("/merge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExactShaIsSentToAtomicProviderMerge()
    {
        var sha = new string('a', 40);
        var mergeSha = new string('c', 40);
        var handler = new SequenceHandler(
            Json(HttpStatusCode.Created, "{\"token\":\"installation-secret\"}"),
            Json(HttpStatusCode.OK, $"{{\"head\":{{\"sha\":\"{sha}\"}}}}"),
            Json(HttpStatusCode.OK, $"{{\"sha\":\"{mergeSha}\",\"merged\":true,\"message\":\"ok\"}}"));
        var client = CreateClient(handler);

        var result = await client.MergePullRequestAsync(
            new GitHubMergeRequest(12, "approved-org", "private-project", 4, sha, "merge-once"),
            CancellationToken.None);

        Assert.True(result.Merged);
        Assert.True(result.HeadMatched);
        Assert.Equal(mergeSha, result.MergeCommitSha);
        var request = Assert.Single(handler.Requests, candidate =>
            candidate.Path.EndsWith("/merge", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(sha, body.RootElement.GetProperty("sha").GetString());
        Assert.Equal("squash", body.RootElement.GetProperty("merge_method").GetString());
        Assert.DoesNotContain("installation-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ProvisioningAlwaysRequestsPrivateAndFixedProtection()
    {
        var handler = new SequenceHandler(
            Json(HttpStatusCode.OK,
                "{\"id\":12,\"account\":{\"id\":99,\"login\":\"approved-org\",\"type\":\"Organization\"},\"suspended_at\":null,\"suspended_by\":null}"),
            Json(HttpStatusCode.Created, "{\"token\":\"installation-secret\"}"),
            Json(HttpStatusCode.Created,
                "{\"id\":42,\"name\":\"private-project\",\"private\":true,\"default_branch\":\"main\",\"owner\":{\"id\":99,\"login\":\"approved-org\",\"type\":\"Organization\"}}"),
            Json(HttpStatusCode.OK, "{}"));
        var client = CreateClient(handler);

        var result = await client.ProvisionPrivateRepositoryAsync(
            new GitHubProvisionRepositoryRequest(
                12, "approved-org", "private-project", "Description",
                "approved", "template", "main", "create-once"),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.False(result.Quarantined);
        var create = Assert.Single(handler.Requests, request =>
            request.Path.EndsWith("/generate", StringComparison.Ordinal));
        using (var body = JsonDocument.Parse(create.Body))
        {
            Assert.True(body.RootElement.GetProperty("private").GetBoolean());
            Assert.False(body.RootElement.GetProperty("include_all_branches").GetBoolean());
        }
        var protection = Assert.Single(handler.Requests, request =>
            request.Path.EndsWith("/protection", StringComparison.Ordinal));
        using (var body = JsonDocument.Parse(protection.Body))
        {
            Assert.False(body.RootElement.GetProperty("allow_force_pushes").GetBoolean());
            Assert.False(body.RootElement.GetProperty("allow_deletions").GetBoolean());
            Assert.True(body.RootElement.GetProperty("enforce_admins").GetBoolean());
        }
        Assert.DoesNotContain(handler.Requests, request =>
            request.Method == HttpMethod.Delete.Method);
    }

    private static GitHubAppClient CreateClient(HttpMessageHandler handler)
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var options = Options.Create(new GitHubAppOptions
        {
            AppId = 123,
            PrivateKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubAppClient(
            http, new GitHubAppCredentialProvider(options), TimeProvider.System);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string Method, string Path, string Body);
}
