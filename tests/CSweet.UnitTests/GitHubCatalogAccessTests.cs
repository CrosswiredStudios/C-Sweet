using System.Net;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class GitHubCatalogAccessTests
{
    private const string Token = "synthetic-catalog-token-0001";

    [Fact]
    public async Task A_verified_token_is_stored_encrypted_and_used_ahead_of_host_configuration()
    {
        await using var f = new Fixture(configured: "synthetic-configured-token");
        Assert.Equal("synthetic-configured-token", await f.Provider.GetTokenAsync(default));
        Assert.Equal("configuration", (await f.Service.GetStatusAsync(default)).Source);

        var status = await f.Service.SaveAsync(Guid.NewGuid(), "  " + Token + " ", default);

        Assert.True(status.Configured); Assert.Equal("settings", status.Source);
        Assert.Equal("example-studio", status.AccountLogin); Assert.Equal("Organization", status.AccountType);
        Assert.Equal(5000, status.RequestLimit); Assert.Equal(4990, status.RequestsRemaining);
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.Zero), status.TokenExpiresAt);
        var row = await f.Db.GitHubCatalogCredentials.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(Token, row.ProtectedToken);
        // Saving invalidates the cache, so the next catalog request already carries the new token.
        Assert.Equal(Token, await f.Provider.GetTokenAsync(default));
        Assert.Contains(f.GitHub.Requests, x => x == ("/rate_limit", Token));
    }

    [Fact]
    public async Task A_token_GitHub_rejects_is_not_stored()
    {
        await using var f = new Fixture(configured: null);
        f.GitHub.Accept = false;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SaveAsync(Guid.NewGuid(), Token, default));
        Assert.DoesNotContain(Token, error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SaveAsync(Guid.NewGuid(), "short", default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SaveAsync(Guid.NewGuid(), "synthetic token with spaces 0001", default));
        Assert.Empty(f.Db.GitHubCatalogCredentials);
        Assert.Null(await f.Provider.GetTokenAsync(default));
    }

    [Fact]
    public async Task Removing_the_token_returns_to_host_configuration_and_replacing_keeps_one_row()
    {
        await using var f = new Fixture(configured: "synthetic-configured-token");
        await f.Service.SaveAsync(Guid.NewGuid(), Token, default);
        await f.Service.SaveAsync(Guid.NewGuid(), Token + "-second", default);
        Assert.Single(f.Db.GitHubCatalogCredentials);
        Assert.Equal(Token + "-second", await f.Provider.GetTokenAsync(default));

        var status = await f.Service.RemoveAsync(default);

        Assert.Equal("configuration", status.Source); Assert.Null(status.AccountLogin);
        Assert.Equal("synthetic-configured-token", await f.Provider.GetTokenAsync(default));
    }

    [Fact]
    public async Task The_credential_reaches_only_the_GitHub_api()
    {
        foreach (var (url, expected) in new[] {
            ("https://api.github.com/repos/example/provider", (string?)Token),
            ("https://github.com/example/provider/releases/download/v1/a.zip", null),
            ("http://api.github.com/repos/example/provider", null),
            ("https://api.github.com.attacker.test/repos/example/provider", null) })
        {
            string? seen = "unset";
            using var handler = new GitHubCatalogAuthenticationHandler(new Fixed(Token)) {
                InnerHandler = new Inspect(request => seen = request.Headers.Authorization?.Parameter) };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(url);
            Assert.Equal(expected, seen);
        }
    }

    [Fact]
    public async Task A_rate_limited_setup_with_no_credential_tells_people_once_a_day_where_to_sign_in()
    {
        await using var f = await ResourceFulfillmentTests.Fixture.CreateAsync();
        f.Service = new ResourceFulfillmentService(f.Db, [f.Handler], f.Clock, catalogCredentials: new Fixed(null));
        f.Handler.PreparationError = new AgentImportPreviewException("GitHub rate limited catalog access.") {
            IsDependencyFailure = true, IsRateLimited = true, RetryAt = f.Clock.GetUtcNow().AddMinutes(2) };
        var request = await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        await f.Service.ProcessNextAsync(default);
        for (var i = 0; i < 20; i++) f.Clock.Advance();
        await f.Service.ProcessNextAsync(default);

        Assert.Equal("WaitingDependency", (await f.ReadAsync(request.Id)).Status);
        var notice = await f.Db.UserNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(f.Manager.Id, notice.RecipientOrganizationUserId);
        Assert.Equal("/settings/source-control?catalogAccess=true", notice.ActionUri);
        Assert.Equal("GitHubCatalogSignInNeeded", notice.Category);
    }

    [Theory]
    [InlineData(true, true)]   // signed in: the limit is the account's own, another sign-in would not help
    [InlineData(false, false)] // not a rate limit: a credential is not the remedy
    public async Task No_sign_in_notice_is_sent_when_signing_in_would_not_help(bool rateLimited, bool signedIn)
    {
        await using var f = await ResourceFulfillmentTests.Fixture.CreateAsync();
        f.Service = new ResourceFulfillmentService(f.Db, [f.Handler], f.Clock, catalogCredentials: new Fixed(signedIn ? Token : null));
        f.Handler.PreparationError = new AgentImportPreviewException("Catalog access is unavailable.") {
            IsDependencyFailure = true, IsRateLimited = rateLimited, RetryAt = f.Clock.GetUtcNow().AddMinutes(2) };
        await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        await f.Service.ProcessNextAsync(default);
        Assert.Empty(f.Db.UserNotifications);
    }

    private sealed class Fixed(string? token) : IGitHubCatalogCredentialProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
        public void Invalidate() { }
    }
    private sealed class Inspect(Action<HttpRequestMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { inspect(request); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
    private sealed class GitHubStub : HttpMessageHandler
    {
        public bool Accept = true;
        public readonly List<(string Path, string? Token)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; var bearer = request.Headers.Authorization?.Parameter;
            Requests.Add((path, bearer));
            if (path == "/user" && !Accept) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var body = path == "/user"
                ? """{"login":"example-studio","type":"Organization"}"""
                : "{\"resources\":{\"core\":{\"limit\":" + (bearer is null ? 60 : 5000) + ",\"remaining\":" +
                    (bearer is null ? 12 : 4990) + ",\"reset\":1800000000}}}";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (path == "/user") response.Headers.TryAddWithoutValidation("github-authentication-token-expiration", "2027-01-02 03:04:05 UTC");
            return Task.FromResult(response);
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public readonly CSweetDbContext Db;
        public readonly GitHubStub GitHub = new();
        public readonly GitHubCatalogCredentialProvider Provider;
        public readonly GitHubCatalogAccessService Service;
        public Fixture(string? configured)
        {
            var database = Guid.NewGuid().ToString();
            _services = new ServiceCollection()
                .AddDbContext<CSweetDbContext>(x => x.UseInMemoryDatabase(database)).BuildServiceProvider();
            Db = _services.GetRequiredService<CSweetDbContext>();
            var protection = new EphemeralDataProtectionProvider();
            var options = Options.Create(new GitHubAgentRepositoryOptions { AccessToken = configured ?? string.Empty });
            Provider = new(_services.GetRequiredService<IServiceScopeFactory>(), protection, options, TimeProvider.System);
            Service = new(Db, new Factory(GitHub), protection, Provider, options, TimeProvider.System);
        }
        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }
}
