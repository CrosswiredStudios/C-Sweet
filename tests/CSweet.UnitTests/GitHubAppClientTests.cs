using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class GitHubAppClientTests
{
    [Theory]
    [InlineData(99, "owner", "repo", true, false)]
    [InlineData(42, "other", "repo", true, false)]
    [InlineData(42, "owner", "other", true, false)]
    [InlineData(42, "owner", "repo", false, false)]
    [InlineData(42, "owner", "repo", true, true)]
    public async Task WorkspacePreparationRejectsReplacedOrUnavailableRepositoryBeforeGitTransfer(
        long repositoryId, string owner, string name, bool isPrivate, bool archived)
    {
        var payload = JsonSerializer.Serialize(new { total_count = 1, repositories = new[] {
            new { id = repositoryId, owner = new { login = owner }, name, full_name = owner + "/" + name,
                clone_url = "https://github.com/owner/repo.git", default_branch = "main", @private = isPrivate, archived, is_template = false } } });
        var handler = new SequenceHandler(Json(HttpStatusCode.Created, "{\"token\":\"installation-secret\"}"), Json(HttpStatusCode.OK, payload));
        var service = new GitHubWorkspaceSnapshotService(CreateClient(handler), new WorkspaceArtifactValidator());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync(
            new(12, 42, "owner", "repo", "main", Guid.NewGuid(), "work/one", null, "prepare")));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WorkspacePreparationRequiresRepositoryIdentityBeforeProviderAccess(long identity)
    {
        var handler = new SequenceHandler();
        var service = new GitHubWorkspaceSnapshotService(CreateClient(handler), new WorkspaceArtifactValidator());
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareAsync(
            new(12, identity, "owner", "repo", "main", Guid.NewGuid(), "work/one", null, "prepare")));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkspacePullRequestCreatesOrReusesExactHead(bool existing)
    {
        var sha = new string('a', 40);
        var pull = JsonSerializer.Serialize(new { number = 7, state = "open", head = new { sha, @ref = "work/one", repo = new { id = 42 } },
            @base = new { @ref = "main", repo = new { id = 42 } }, html_url = "https://untrusted.invalid/" });
        var handler = existing ? new SequenceHandler(Json(HttpStatusCode.OK, "[" + pull + "]"))
            : new SequenceHandler(Json(HttpStatusCode.OK, "[]"), Json(HttpStatusCode.Created, pull));
        var request = new CSweet.Contracts.SourceControl.GitHubSnapshotOperation(12, 42, "owner", "repo",
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "publish", sha, "work/one", "main", "once", [], "", 0, 0, "Feature"), "Feature", "Description");
        Assert.Equal("https://github.com/owner/repo/pull/7", await CreateClient(handler).EnsureWorkspacePullRequestAsync(request, sha, "secret", CancellationToken.None));
        Assert.Equal(existing ? 1 : 2, handler.Requests.Count);
        if (!existing)
        {
            using var body = JsonDocument.Parse(handler.Requests.Last().Body);
            Assert.Equal("work/one", body.RootElement.GetProperty("head").GetString());
            Assert.False(body.RootElement.GetProperty("maintainer_can_modify").GetBoolean());
        }
    }

    [Theory]
    [InlineData("closed", 42, false)]
    [InlineData("open", 99, false)]
    [InlineData("open", 42, true)]
    public async Task WorkspacePullRequestRejectsClosedForeignOrChangedHead(string state, int repoId, bool changed)
    {
        var sha = new string('a', 40);
        var pull = JsonSerializer.Serialize(new { number = 7, state, head = new { sha = changed ? new string('b', 40) : sha, @ref = "work/one", repo = new { id = repoId } },
            @base = new { @ref = "main", repo = new { id = 42 } } });
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, "[" + pull + "]"));
        var request = new CSweet.Contracts.SourceControl.GitHubSnapshotOperation(12, 42, "owner", "repo",
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "publish", sha, "work/one", "main", "once", [], "", 0, 0, "Feature"), "Feature");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient(handler).EnsureWorkspacePullRequestAsync(request, sha, "secret", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

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
