using System.Net;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PluginConnectionCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectDisablesDependentAuthorityBeforeRemoteWorkAndRetriesDurably(bool interrupt)
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Connector.PackageVersion!.ManifestJson, Json)! with
        { Setup = new() { EntryFlow = "setup", Required = true, Flows = [new() { Id = "setup", Steps = [new()
            { Id = "connect", Kind = "oauth-connect", Connection = "account", ScopeSet = "base" }] }] } };
        f.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, Json);
        var policy = new PluginStandingPolicy { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            AgentInstallationId = f.Requester.Id, Status = PluginStandingPolicyStatus.Approved };
        f.Db.PluginStandingPolicies.Add(policy);
        var plan = await f.Prepare("one");
        plan.ResultJson = "{\"providerText\":\"must be purged\"}";
        await f.Db.SaveChangesAsync();
        var secrets = new Secrets(); var profiles = new Profiles();
        var key = $"oauth.connection.{f.Connection.Id:N}.token";
        secrets.Values[key] = "{\"accessToken\":\"fake-access\",\"refreshToken\":\"fake-refresh\"}";
        using var http = new Http(() =>
        {
            Assert.False(f.Connector.IsEnabled);
            Assert.True(f.Requester.IsEnabled); // Recovery conversation remains available.
            Assert.Equal(PluginStandingPolicyStatus.Revoked, policy.Status);
            Assert.Equal("Cancelled", plan.Status); Assert.Null(plan.ResultJson);
            Assert.Null(f.Connection.BoundResourceId);
            Assert.DoesNotContain(key, secrets.Values.Keys);
            if (interrupt) throw new OperationCanceledException();
        });
        http.Status = HttpStatusCode.ServiceUnavailable;
        var service = new PluginSetupService(f.Db, secrets, new EphemeralDataProtectionProvider(), http,
            null!, profiles, null!, null!, new Audit());
        await service.DisconnectAsync(f.Organization, f.Connector.Id, "account");
        var job = await f.Db.PluginOperationalStates.SingleAsync(x => x.Kind == PluginConnectionCleanupService.PendingKind);
        Assert.DoesNotContain("fake-refresh", job.PayloadJson);
        Assert.Single(secrets.Values); // Quarantined for revocation; not usable by the credential broker.
        http.BeforeSend = null; http.Status = HttpStatusCode.OK;
        var state = JsonSerializer.Deserialize<PluginConnectionCleanupService.Cleanup>(job.PayloadJson, Json)!;
        PluginConnectionCleanupService.Save(job, state with { NextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        await f.Db.SaveChangesAsync();
        await new PluginConnectionCleanupService(f.Db, secrets, http, profiles, new Audit()).ProcessPendingAsync(default);
        Assert.Equal(PluginConnectionCleanupService.CompletedKind, job.Kind);
        Assert.Contains("RevocationConfirmed", job.PayloadJson);
        Assert.Empty(secrets.Values);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task ChangedProviderDestinationNeverReceivesQuarantinedCredentials()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        f.Connection.Status = PluginConnectionStatus.Revoked;
        var job = Job(f);
        f.Db.PluginOperationalStates.Add(job); await f.Db.SaveChangesAsync();
        var secrets = new Secrets();
        secrets.Values[$"oauth.connection.{f.Connection.Id:N}.token"] = "{\"accessToken\":\"private-token\"}";
        var profiles = new Profiles { Endpoint = "https://different.example/revoke" };
        using var http = new Http();
        var service = new PluginConnectionCleanupService(f.Db, secrets, http, profiles, new Audit());
        await service.ProcessPendingAsync(default);
        Assert.Equal(0, http.Calls);
        Assert.Equal(PluginConnectionCleanupService.PendingKind, job.Kind);
        var state = JsonSerializer.Deserialize<PluginConnectionCleanupService.Cleanup>(job.PayloadJson, Json)!;
        PluginConnectionCleanupService.Save(job, state with { NextAttempt = DateTimeOffset.UtcNow.AddSeconds(-1), Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) });
        await f.Db.SaveChangesAsync();
        await service.ProcessPendingAsync(default);
        Assert.Equal(0, http.Calls); Assert.Empty(secrets.Values);
        Assert.Contains("RevocationUnconfirmed", job.PayloadJson);
    }

    [Fact]
    public async Task CancelledCleanupRemainsDurableAndDoesNotClaimCompletion()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        f.Connection.Status = PluginConnectionStatus.Revoked;
        var job = Job(f); f.Db.PluginOperationalStates.Add(job); await f.Db.SaveChangesAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var http = new Http();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PluginConnectionCleanupService(f.Db,
            new Secrets(), http, new Profiles(), new Audit()).ProcessPendingAsync(cancelled.Token));
        Assert.Equal(PluginConnectionCleanupService.PendingKind, job.Kind);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task BrokerReadsOnlyActiveGenerationAndNeverReturnsRevokedCredentials()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        var generation = Guid.NewGuid();
        PluginOAuthCredentialKeys.Register(f.Db, f.Organization, f.Connector.Id, f.Connection.Id, generation);
        await PluginOAuthCredentialKeys.SelectAsync(f.Db, f.Organization, f.Connector.Id, f.Connection.Id, generation, default);
        await f.Db.SaveChangesAsync();
        var secrets = new Secrets();
        var key = PluginOAuthCredentialKeys.Generation(f.Connection.Id, generation);
        secrets.Values[key] = JsonSerializer.Serialize(new { accessToken = "current", refreshToken = "refresh", tokenType = "Bearer", expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        secrets.Values[PluginOAuthCredentialKeys.Legacy(f.Connection.Id)] = JsonSerializer.Serialize(new { accessToken = "stale", expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        using var http = new Http();
        var broker = new PluginOAuthTokenBroker(secrets, f.Db, http, new Profiles());
        Assert.Equal("current", await broker.GetAccessTokenAsync(f.Connector.Id, f.Connection));
        f.Connection.Status = PluginConnectionStatus.Revoked; await f.Db.SaveChangesAsync();
        Assert.Null(await broker.GetAccessTokenAsync(f.Connector.Id, f.Connection));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task CleanupQuarantinesSelectedGenerationAndRemovesUnusedAttemptMaterial()
    {
        await using var f = await ConnectorPlanServiceTests.Fixture.Create();
        f.Connection.Status = PluginConnectionStatus.Revoked;
        var job = Job(f); var active = PluginOAuthCredentialKeys.Generation(f.Connection.Id, Guid.NewGuid());
        var abandoned = PluginOAuthCredentialKeys.Generation(f.Connection.Id, Guid.NewGuid());
        var state = JsonSerializer.Deserialize<PluginConnectionCleanupService.Cleanup>(job.PayloadJson, Json)!;
        PluginConnectionCleanupService.Save(job, state with { ActiveCredentialKey = active, SecretKeys = [abandoned] });
        f.Db.PluginOperationalStates.Add(job); await f.Db.SaveChangesAsync();
        var secrets = new Secrets(); secrets.Values[active] = "{\"accessToken\":\"active-token\"}"; secrets.Values[abandoned] = "unused-credential";
        using var http = new Http();
        await new PluginConnectionCleanupService(f.Db, secrets, http, new Profiles(), new Audit()).ProcessPendingAsync(default);
        Assert.Empty(secrets.Values); Assert.Equal(1, http.Calls);
        Assert.Contains("RevocationConfirmed", job.PayloadJson);
    }

    private static PluginOperationalState Job(ConnectorPlanServiceTests.Fixture f)
    {
        var job = new PluginOperationalState { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            AgentInstallationId = f.Connector.Id, Kind = PluginConnectionCleanupService.PendingKind, ExternalKey = f.Connection.Id.ToString("N") };
        PluginConnectionCleanupService.Save(job, new(f.Connection.Id, "account", "example.profile", "https://identity.example.com/authorize",
            "https://identity.example.com/token", "https://identity.example.com/revoke", [], DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow));
        return job;
    }
    private sealed class Secrets : IPluginSecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task SetAsync(Guid id, string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(Guid id, string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task RemoveAsync(Guid id, string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }
    private sealed class Profiles : IPluginProviderProfileRegistry
    {
        public string Endpoint { get; set; } = "https://identity.example.com/revoke";
        public Task<PluginOAuthProviderProfile?> ResolveAsync(string id, CancellationToken ct = default) => Task.FromResult<PluginOAuthProviderProfile?>(
            new(id, "Example", "https://identity.example.com/authorize", "https://identity.example.com/token", Endpoint, "client", "secret"));
        public Task<IReadOnlyList<PluginProviderProfileResponse>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginProviderProfileResponse> UpsertAsync(string id, UpsertPluginProviderProfileRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Http(Action? before = null) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public Action? BeforeSend { get; set; } = before;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; BeforeSend?.Invoke(); Assert.Equal("https://identity.example.com/revoke", request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(Status)); }
    }
    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string type, string entity, Guid? id, string? summary, string? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
