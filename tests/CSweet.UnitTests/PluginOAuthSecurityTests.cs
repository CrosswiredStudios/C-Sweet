using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CSweet.UnitTests;

public sealed class PluginOAuthSecurityTests
{
    [Fact]
    public async Task ConsentUsesPkceExactRedirectAndOnlyRequestedScopes()
    {
        await using var f = await Fixture.Create();
        var begin = await f.Begin();
        var query = QueryHelpers.ParseQuery(new Uri(begin.AuthorizationUrl).Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Fixture.Redirect, query["redirect_uri"]);
        Assert.Equal("read", query["scope"]);
        var result = await f.Service.CompleteAuthorizationAsync(f.UserId, "fake-code", query["state"]!);
        Assert.Equal(f.Inner.Organization, result.OrganizationId);
        var sent = QueryHelpers.ParseQuery(f.Http.Form!);
        Assert.Equal(Fixture.Redirect, sent["redirect_uri"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(sent["code_verifier"]!)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        Assert.Equal("[\"read\"]", f.Inner.Connection.GrantedScopesJson);
        Assert.Equal("select", f.Inner.Connector.SetupStepId);
        Assert.Equal(PluginSetupState.NeedsSetup, f.Inner.Connector.SetupState);
        Assert.DoesNotContain(f.Secrets.Values.Keys, x => x.Contains("verifier"));
        Assert.DoesNotContain("fake-access-token", JsonSerializer.Serialize(result));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "fake-code", query["state"]!));
        Assert.Equal(1, f.Http.Count);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("manifest")]
    [InlineData("profile")]
    [InlineData("grant")]
    [InlineData("step")]
    [InlineData("disconnect")]
    [InlineData("membership")]
    [InlineData("redirect")]
    public async Task ChangedConsentContextCannotExchangeCode(string change)
    {
        await using var f = await Fixture.Create();
        var state = await f.State();
        switch (change)
        {
            case "digest": f.Inner.Connector.PackageVersion!.PackageDigest = new string('b', 64); break;
            case "manifest": f.Inner.Connector.PackageVersion!.ManifestJson += " "; break;
            case "profile": f.Profiles.Profile = f.Profiles.Profile with { ClientId = "other-client" }; break;
            case "grant": f.Inner.Connector.Grant!.GrantRevision++; break;
            case "step": f.Inner.Connector.SetupStepId = "select"; break;
            case "disconnect": f.Inner.Connection.Status = PluginConnectionStatus.Revoked; break;
            case "membership": f.User.IsActive = false; break;
            case "redirect": (await f.Inner.Db.PluginOAuthAttempts.SingleAsync()).RedirectUri = "https://attacker.example/callback"; break;
        }
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "fake-code", state));
        Assert.Equal(0, f.Http.Count);
        Assert.DoesNotContain(f.Secrets.Values.Keys, x => x.Contains("connection"));
    }

    [Fact]
    public async Task ScopeEscalationRequiresDeclaredCurrentAction()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Begin("manage"));
        Assert.Empty(await f.Inner.Db.PluginOAuthAttempts.ToListAsync());
    }

    [Fact]
    public async Task WrongHumanTamperedAndExpiredStatesFailBeforeExchange()
    {
        await using var f = await Fixture.Create();
        var state = await f.State();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteAuthorizationAsync(Guid.NewGuid(), "code", state));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "code", state + "tampered"));
        (await f.Inner.Db.PluginOAuthAttempts.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "code", state));
        Assert.Equal(0, f.Http.Count);
    }

    [Fact]
    public async Task RevocationDuringTokenExchangeDiscardsNewCredentials()
    {
        await using var f = await Fixture.Create();
        var state = await f.State();
        f.Http.AfterRequest = async () => { f.User.IsActive = false; await f.Inner.Db.SaveChangesAsync(); };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "code", state));
        Assert.Empty(f.Secrets.Values);
    }

    [Fact]
    public async Task ProgressiveConsentRevalidatesBoundAccountAndNeverMixesRefreshTokens()
    {
        await using var f = await Fixture.Create(ready: true);
        var policy = new PluginStandingPolicy { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization,
            AgentInstallationId = f.Inner.Requester.Id, ChannelId = "confirmed", Status = PluginStandingPolicyStatus.Approved };
        f.Inner.Db.PluginStandingPolicies.Add(policy);
        await f.Inner.Db.SaveChangesAsync();
        f.Secrets.Values[$"oauth.connection.{f.Inner.Connection.Id:N}.token"] = "{\"refreshToken\":\"old-principal-refresh\"}";
        var revision = f.Inner.Connector.Grant!.GrantRevision;
        var state = await f.State("manage");
        await f.Service.CompleteAuthorizationAsync(f.UserId, "code", state);
        Assert.Equal(PluginSetupState.NeedsSetup, f.Inner.Connector.SetupState);
        Assert.Equal("select", f.Inner.Connector.SetupStepId);
        Assert.Equal("confirmed", f.Inner.Connection.BoundResourceId);
        Assert.Equal(revision + 1, f.Inner.Connector.Grant.GrantRevision);
        Assert.DoesNotContain("old-principal-refresh", Assert.Single(f.Secrets.Values).Value);
        using var stored = JsonDocument.Parse(Assert.Single(f.Secrets.Values).Value);
        Assert.Equal(JsonValueKind.Null, stored.RootElement.GetProperty("refreshToken").ValueKind);
        Assert.Equal("[\"manage\",\"read\"]", f.Inner.Connection.GrantedScopesJson);
        Assert.Equal(PluginStandingPolicyStatus.Revoked, policy.Status);
    }

    [Fact]
    public async Task MissingRequestedScopeDoesNotRetainOldAuthority()
    {
        await using var f = await Fixture.Create(ready: true);
        var state = await f.State("manage");
        f.Http.Response = "{\"access_token\":\"fake-access-token\",\"scope\":\"manage\"}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "code", state));
        Assert.Empty(f.Secrets.Values);
        Assert.Equal("[\"read\"]", f.Inner.Connection.GrantedScopesJson);
    }

    [Theory]
    [InlineData("{\"access_token\":\"one\",\"access_token\":\"two\",\"scope\":\"read\"}")]
    [InlineData("{\"access_token\":\"one\",\"scope\":\"read\",\"token_type\":\"MAC\"}")]
    public async Task AmbiguousOrUnsupportedTokenResponseIsNotStored(string response)
    {
        await using var f = await Fixture.Create();
        var state = await f.State();
        f.Http.Response = response;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "code", state));
        Assert.Empty(f.Secrets.Values);
    }

    [Fact]
    public async Task DisconnectDuringVaultWriteCannotBeOverwrittenByCallback()
    {
        await using var f = await Fixture.Create();
        var state = await f.State();
        var options = (DbContextOptions<CSweetDbContext>)f.Inner.Db.GetService<IDbContextOptions>();
        f.Secrets.AfterSet = async () =>
        {
            await using var other = new CSweetDbContext(options);
            var connection = await other.PluginConnections.SingleAsync();
            connection.Status = PluginConnectionStatus.Revoked;
            connection.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await other.SaveChangesAsync();
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteAuthorizationAsync(f.UserId, "code", state));
        await using var verify = new CSweetDbContext(options);
        Assert.Equal(PluginConnectionStatus.Revoked, (await verify.PluginConnections.SingleAsync()).Status);
    }

    [Fact]
    public async Task TwoCallbacksWithStaleTrackedAttemptExchangeOnlyOnce()
    {
        await using var f = await Fixture.Create();
        var state = await f.State();
        var options = (DbContextOptions<CSweetDbContext>)f.Inner.Db.GetService<IDbContextOptions>();
        await using var otherDb = new CSweetDbContext(options);
        _ = await otherDb.PluginOAuthAttempts.SingleAsync();
        await f.Service.CompleteAuthorizationAsync(f.UserId, "code", state);
        var other = f.NewService(otherDb);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.CompleteAuthorizationAsync(f.UserId, "code", state));
        Assert.Equal(1, f.Http.Count);
    }

    [Fact]
    public async Task RelationalSingleUseUpdateRejectsCompetingContext()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseSqlite(connection).Options;
        await using var first = new CSweetDbContext(options);
        // Only the table under test is created, in an isolated in-memory relational database.
        await first.Database.ExecuteSqlRawAsync("""
            CREATE TABLE PluginOAuthAttempts (
                Id TEXT PRIMARY KEY, AgentInstallationId TEXT NOT NULL, ApplicationUserId TEXT NOT NULL,
                ConnectionDeclarationId TEXT NOT NULL, ScopeSetId TEXT NOT NULL, StateHash TEXT NOT NULL,
                RedirectUri TEXT NOT NULL, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, ConsumedAt TEXT NULL)
            """);
        first.PluginOAuthAttempts.Add(new() { Id = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        await first.SaveChangesAsync();
        await using var second = new CSweetDbContext(options);
        var stale = await second.PluginOAuthAttempts.SingleAsync();
        (await first.PluginOAuthAttempts.SingleAsync()).ConsumedAt = DateTimeOffset.UtcNow;
        await first.SaveChangesAsync();
        stale.ConsumedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Redirect = "https://host.example/api/plugin-connections/oauth/callback";
        public ConnectorPlanServiceTests.Fixture Inner { get; private init; } = null!;
        public Guid UserId { get; } = Guid.NewGuid();
        public OrganizationUser User { get; private set; } = null!;
        public SecretStore Secrets { get; } = new();
        public Profiles Profiles { get; } = new();
        public FakeHttp Http { get; } = new();
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        public PluginSetupService Service => NewService(Inner.Db);
        public PluginSetupService NewService(CSweetDbContext db) => new(db, Secrets, protection, Http,
            null!, Profiles, null!, null!, new Audit());
        public Task<BeginPluginAuthorizationResponse> Begin(string scope = "base") => Service.BeginAuthorizationAsync(
            Inner.Organization, UserId, Inner.Connector.Id, "account", new(scope), Redirect);
        public async Task<string> State(string scope = "base") => QueryHelpers.ParseQuery(new Uri((await Begin(scope)).AuthorizationUrl).Query)["state"]!;
        public ValueTask DisposeAsync() { Http.Dispose(); return Inner.DisposeAsync(); }
        public static async Task<Fixture> Create(bool ready = false)
        {
            var f = new Fixture { Inner = await ConnectorPlanServiceTests.Fixture.Create() };
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Inner.Connector.PackageVersion!.ManifestJson, options)!;
            manifest = manifest with
            {
                Connections = [manifest.Connections[0] with { ScopeSets = [new() { Id = "base", Required = true, Scopes = ["read"] }, new() { Id = "manage", Scopes = ["manage"] }] }],
                Setup = new() { Required = true, EntryFlow = "connect", Flows = [
                    new() { Id = "connect", Steps = [new() { Id = "oauth", Kind = "oauth-connect", Connection = "account", ScopeSet = "base" },
                        new() { Id = "select", Kind = "account-selector", Connection = "account" }, new() { Id = "validate", Kind = "health-check", Connection = "account" }] },
                    new() { Id = "settings", Steps = [new() { Id = "manage", Kind = "permission-request", Connection = "account", ScopeSet = "manage" }] }
                ] }, Ui = [new() { Kind = "personal-settings", Flow = "settings" }]
            };
            f.Inner.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, options);
            f.Inner.Connector.SetupState = ready ? PluginSetupState.Ready : PluginSetupState.NeedsSetup;
            f.Inner.Connector.SetupFlowId = "connect"; f.Inner.Connector.SetupStepId = ready ? null : "oauth";
            f.Inner.Connector.SetupDataJson = ready ? "{\"completedStepIds\":[\"oauth\",\"select\",\"validate\"]}" : "{}";
            f.User = new() { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, ApplicationUserId = f.UserId,
                PermissionLevel = OrganizationPermissionLevel.Owner, EmployeeType = EmployeeType.Human };
            f.Inner.Db.CoreOrganizationUsers.Add(f.User);
            await f.Inner.Db.SaveChangesAsync();
            return f;
        }
    }

    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Count { get; private set; }
        public string? Form { get; private set; }
        public string Response { get; set; } = "{\"access_token\":\"fake-access-token\",\"scope\":\"read manage extra-provider-scope\",\"expires_in\":3600}";
        public Func<Task>? AfterRequest { get; set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://identity.example.com/token", request.RequestUri!.AbsoluteUri);
            Count++; Form = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (AfterRequest is not null) await AfterRequest();
            return new(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }
    }
    private sealed class SecretStore : IPluginSecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Func<Task>? AfterSet { get; set; }
        public async Task SetAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default)
        { Values[key] = value; if (AfterSet is not null) await AfterSet(); }
        public Task<string?> GetAsync(Guid installationId, string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task RemoveAsync(Guid installationId, string key, CancellationToken cancellationToken = default) { Values.Remove(key); return Task.CompletedTask; }
    }
    private sealed class Profiles : IPluginProviderProfileRegistry
    {
        public PluginOAuthProviderProfile Profile { get; set; } = new("example.profile", "Example", "https://identity.example.com/authorize",
            "https://identity.example.com/token", "https://identity.example.com/revoke", "fake-client", "fake-client-secret");
        public Task<PluginOAuthProviderProfile?> ResolveAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<PluginOAuthProviderProfile?>(id == Profile.Id ? Profile : null);
        public Task<IReadOnlyList<PluginProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PluginProviderProfileResponse> UpsertAsync(string id, UpsertPluginProviderProfileRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, string entityType, Guid? entityId, string? summary,
            string? metadataJson = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
