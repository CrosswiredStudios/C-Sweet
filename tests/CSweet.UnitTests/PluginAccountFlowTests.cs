using System.Net;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class PluginAccountFlowTests
{
    [Fact]
    public async Task FullStoryConnectImportGrantBindReadAndRevokeNeverGrantsAmbientAccess()
    {
        await using var f = await Fixture.Create();
        var empty = await f.Service.GetAsync(f.Admin, f.Id, default); Assert.Empty(empty.Accounts); Assert.True(empty.CanConnect);
        var begin = await f.Begin(); var query = QueryHelpers.ParseQuery(new Uri(begin.AuthorizationUrl).Query);
        Assert.Equal("S256", query["code_challenge_method"]); Assert.Equal(new[] { "identity", "read" }, query["scope"].ToString().Split(' ').Order());
        Assert.DoesNotContain("secret", begin.AuthorizationUrl); Assert.Equal("AwaitingConsent", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync()).Status);
        await f.Service.CompleteAsync(f.Admin, "provider-code", query["state"]!, null, default);
        Assert.Empty(await f.Inner.Db.PluginAccountBusinessGrants.ToArrayAsync());
        await f.Service.ImportAsync(begin.SetupId, default);
        var view = await f.Service.GetAsync(f.Admin, f.Id, default); var account = Assert.Single(view.Accounts);
        Assert.True(account.ProviderVerified); Assert.Equal(2, account.Resources.Count); Assert.Empty(account.BusinessGrants);
        Assert.Equal("Ready", Assert.Single(view.SetupSessions).Status); Assert.Equal(2, f.Transport.Pages);
        await f.Service.SaveGrantAsync(f.Admin, f.Id, account.Id, new(f.Business, "SelectedResources", [account.Resources[0].Id], ["read"], false, null, f.Digest), default);
        var grant = await f.Inner.Db.PluginAccountBusinessGrants.SingleAsync();
        var business = Assert.Single(await f.Service.GetBusinessAccountsAsync(f.Manager, f.Business, f.Id, default));
        Assert.Single(business.Resources); Assert.DoesNotContain("Workspace B", business.Resources.Select(x => x.Name));
        Assert.Empty(business.Authorizations);
        var authorization = Assert.Single(account.Authorizations);
        await f.Service.BindBusinessResourceAsync(f.Manager, f.Business, f.Id, new(f.Inner.Requester.Id, "account", account.Resources[0].Id, authorization.Id, grant.Revision, f.Digest), default);
        var plan = await f.Inner.Prepare("search"); Assert.Equal("workspace-a", plan.ResourceId);
        await f.Service.RevokeGrantAsync(f.Admin, f.Id, grant.Id, grant.Revision, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Inner.Service.RevalidateAsync(f.Business, f.Inner.Requester.Id, plan.Id, plan.PlanHash, default));
        Assert.Equal(PluginConnectionStatus.Connected, (await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == authorization.Id)).Status);
    }

    [Fact]
    public async Task OptionalConsentCanBeDeclinedButRequiredConsentCannot()
    {
        await using var f = await Fixture.Create(); f.Http.Scopes = "read identity";
        var begin = await f.Begin(["base", "optional"]);
        await f.Complete(begin); await f.Service.ImportAsync(begin.SetupId, default);
        var account = Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts);
        Assert.DoesNotContain("optional", Assert.Single(account.Authorizations).GrantedScopeSets);
        var second = await f.Begin(); f.Http.Scopes = "identity"; await f.Complete(second);
        Assert.Equal("Failed", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(s => s.Id == second.SetupId)).Status);
    }

    [Fact]
    public async Task CallbackIsBoundToHumanAndSingleUse()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); var state = State(begin);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CompleteAsync(f.Manager, "code", state, null, default));
        await f.Complete(begin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Complete(begin)); Assert.Equal(1, f.Http.Exchanges);
    }

    [Fact]
    public async Task ChangedProfileOrPackageCannotConsumeReviewedConsent()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); f.Profiles.ClientId = "different-client";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Complete(begin)); Assert.Equal(0, f.Http.Exchanges);
    }

    [Fact]
    public async Task DenialReturnsResumablePageWithoutUsablePartialConnection()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin();
        var completed = await f.Service.CompleteAsync(f.Admin, null, State(begin), "access_denied", default);
        Assert.Equal(f.Id, completed.InstallationId); Assert.Empty(f.Secrets.Values);
        Assert.Equal("Cancelled", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync()).Status);
        Assert.Empty(await f.Inner.Db.PluginAccountResources.ToArrayAsync());
    }

    [Fact]
    public async Task InterruptedDiscoveryRetainsCursorAndNeverExposesPartialInventory()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin); f.Transport.FailSecondPage = true;
        await f.Service.ImportAsync(begin.SetupId, default);
        var pending = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(); Assert.Equal("ImportFailed", pending.Status); Assert.Equal("page-two", pending.PageToken);
        Assert.Empty(await f.Inner.Db.PluginAccountResources.ToArrayAsync());
        f.Transport.FailSecondPage = false;
        await f.Service.RetryImportAsync(f.Admin, f.Id, begin.SetupId, default); await f.Service.ImportAsync(begin.SetupId, default);
        Assert.Equal("Ready", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync()).Status); Assert.Equal(2, await f.Inner.Db.PluginAccountResources.CountAsync());
        Assert.Equal(1, f.Transport.FirstPageRequests);
    }

    [Fact]
    public async Task WholeAccountRequiresExplicitFutureAcknowledgementAndDoesNotExpandSelectedGrant()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin); await f.Service.ImportAsync(begin.SetupId, default);
        var account = Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SaveGrantAsync(f.Admin, f.Id, account.Id,
            new(f.Business, "WholeAccount", [], ["read"], false, null, f.Digest), default));
        await f.Service.SaveGrantAsync(f.Admin, f.Id, account.Id, new(f.Business, "WholeAccount", [], ["read"], true, null, f.Digest), default);
        Assert.NotNull((await f.Inner.Db.PluginAccountBusinessGrants.SingleAsync()).FutureResourcesAcknowledgedAt);
    }

    [Fact]
    public async Task OrdinaryBusinessManagerCannotConnectOrEditEnterpriseGrants()
    {
        await using var f = await Fixture.Create();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.BeginAsync(f.Manager, f.Id, new(["base"], "v1", true, f.Digest), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveGrantAsync(f.Manager, f.Id, Guid.NewGuid(), new(f.Business, "WholeAccount", [], ["read"], true, null, f.Digest), default));
    }

    [Fact]
    public async Task ReconnectRejectsDifferentIdentityAndLeavesExistingAuthorizationUsable()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin); await f.Service.ImportAsync(begin.SetupId, default);
        var original = (await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts.Single().Authorizations.Single();
        var reconnect = await f.Begin(reconnect: original.Id); await f.Complete(reconnect); f.Transport.Subject = "other-subject";
        await f.Service.ImportAsync(reconnect.SetupId, default);
        Assert.Equal("ImportFailed", (await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(x => x.Id == reconnect.SetupId)).Status);
        Assert.Equal(PluginConnectionStatus.Connected, (await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == original.Id)).Status);
    }

    private static string State(BeginPluginAccountResponse begin) => QueryHelpers.ParseQuery(new Uri(begin.AuthorizationUrl).Query)["state"]!;
    [Fact]
    public async Task ReconnectSameSubjectPreservesRefreshWithoutRestoringOldAuthority()
    {
        await using var f = await Fixture.Create(); var first = await f.Begin(); await f.Complete(first); await f.Service.ImportAsync(first.SetupId, default);
        var account = Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts); var old = Assert.Single(account.Authorizations);
        f.Http.RefreshToken = null;
        var next = await f.Begin(reconnect: old.Id); await f.Complete(next); await f.Service.ImportAsync(next.SetupId, default);
        var updated = Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts);
        Assert.Equal(account.Id, updated.Id); Assert.NotEqual(old.Id, Assert.Single(updated.Authorizations).Id);
        Assert.Equal(PluginConnectionStatus.Revoked, (await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == old.Id)).Status);
        var current = await f.Inner.Db.PluginConnections.SingleAsync(x => x.Id == updated.Authorizations[0].Id);
        var token = await f.Secrets.GetAsync(f.Id, PluginOAuthCredentialKeys.Generation(current.Id, current.ActiveCredentialAttemptId!.Value));
        Assert.Contains("host-only-refresh", token);
    }

    [Fact]
    public async Task ExpiredAttemptsRemoveVaultedVerifiersAndCannotBeReplayed()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin();
        var attempt = await f.Inner.Db.PluginAccountSetupSessions.SingleAsync(); attempt.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); await f.Inner.Db.SaveChangesAsync();
        await f.Service.ProcessPendingAsync(default); Assert.Empty(f.Secrets.Values); Assert.Equal("Expired", attempt.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Complete(begin));
    }

    [Fact]
    public async Task DisconnectChecksReviewedImpactAndStopsAccessBeforeDurableRevocation()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin); await f.Service.ImportAsync(begin.SetupId, default);
        var account = Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts);
        var impact = await f.Service.GetDisconnectImpactAsync(f.Admin, f.Id, account.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DisconnectAsync(f.Admin, f.Id, account.Id, new(impact.AccountRevision,"tampered"), default));
        await f.Service.DisconnectAsync(f.Admin, f.Id, account.Id, new(impact.AccountRevision,impact.ReviewHash), default);
        Assert.All(await f.Inner.Db.PluginConnections.Where(x=>x.AccountGroupId==account.Id).ToArrayAsync(), x => Assert.Equal(PluginConnectionStatus.Revoked,x.Status));
        Assert.Equal(PluginConnectionStatus.Connected,(await f.Inner.Db.PluginConnections.SingleAsync(x=>x.AccountGroupId==null)).Status);
        await f.Service.ProcessPendingAsync(default); Assert.Empty(f.Secrets.Values);
        Assert.Equal("RevokedDataDeletionPending", (await f.Inner.Db.PluginAccountRevocationJobs.SingleAsync()).Status);
        Assert.Equal(PluginAccountState.Disconnecting, (await f.Inner.Db.PluginAccountGroups.SingleAsync()).State);
    }

    [Fact]
    public async Task RefreshScopeLossRequiresReconnectWithoutDisablingOtherAccounts()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin); await f.Service.ImportAsync(begin.SetupId, default);
        var c = await f.Inner.Db.PluginConnections.SingleAsync(x=>x.AccountGroupId!=null); var generation = c.ActiveCredentialAttemptId!.Value;
        await f.Secrets.SetAsync(f.Id, PluginOAuthCredentialKeys.Generation(c.Id,generation), JsonSerializer.Serialize(new {
            accessToken="expired", refreshToken="refresh", tokenType="Bearer", expiresAt=DateTimeOffset.UtcNow.AddMinutes(-5) }));
        f.Http.Scopes="identity";
        Assert.Null(await new PluginAccountTokenBroker(f.Inner.Db,f.Secrets,f.Http,f.Profiles).GetAsync(f.Id,c.Id,default));
        Assert.Equal(PluginConnectionStatus.ReauthorizationRequired,c.Status); Assert.True(f.Inner.Connector.IsEnabled);
    }

    [Fact]
    public async Task LockedOutOperatorCannotReadAccountManagement()
    {
        await using var f = await Fixture.Create(); var user = await f.Inner.Db.Users.SingleAsync(x=>x.Id==f.Manager);
        f.Inner.Db.PluginAccountOperators.Add(new() { Id=Guid.NewGuid(),PluginInstallationId=f.Id,ApplicationUserId=f.Manager,GrantedByApplicationUserId=f.Admin });
        user.LockoutEnabled=true; user.LockoutEnd=DateTimeOffset.UtcNow.AddHours(1); await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.GetAsync(f.Manager,f.Id,default));
    }

    [Fact]
    public async Task RefreshRechecksProfileAfterProviderResponseBeforeSelectingNewCredential()
    {
        await using var f = await Fixture.Create(); var begin = await f.Begin(); await f.Complete(begin); await f.Service.ImportAsync(begin.SetupId, default);
        var c = await f.Inner.Db.PluginConnections.SingleAsync(x=>x.AccountGroupId!=null); var generation=c.ActiveCredentialAttemptId!.Value;
        await f.Secrets.SetAsync(f.Id,PluginOAuthCredentialKeys.Generation(c.Id,generation),JsonSerializer.Serialize(new {
            accessToken="expired",refreshToken="refresh",tokenType="Bearer",expiresAt=DateTimeOffset.UtcNow.AddMinutes(-5) }));
        f.Http.BeforeResponse=()=>f.Profiles.ClientId="replaced-during-refresh";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>new PluginAccountTokenBroker(f.Inner.Db,f.Secrets,f.Http,f.Profiles).GetAsync(f.Id,c.Id,default));
        Assert.Equal(generation,c.ActiveCredentialAttemptId); Assert.Null(c.RefreshLeaseOwner);
    }

    [Fact]
    public async Task SameDisplayNameNeverCombinesDifferentVerifiedSubjects()
    {
        await using var f = await Fixture.Create(); var first=await f.Begin(); await f.Complete(first); await f.Service.ImportAsync(first.SetupId,default);
        f.Transport.Subject="a-separate-provider-subject";
        var second=await f.Begin(); await f.Complete(second); await f.Service.ImportAsync(second.SetupId,default);
        var view=await f.Service.GetAsync(f.Admin,f.Id,default);
        Assert.Equal(2,view.Accounts.Count); Assert.Single(view.Accounts.Select(x=>x.Name).Distinct());
        Assert.Equal(2,view.Accounts.Select(x=>x.Id).Distinct().Count()); Assert.All(view.Accounts,x=>Assert.Empty(x.BusinessGrants));
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public ConnectorPlanServiceTests.Fixture Inner { get; private init; } = null!;
        public Guid Admin { get; } = Guid.NewGuid(); public Guid Manager { get; } = Guid.NewGuid();
        public Guid Id => Inner.Connector.Id; public Guid Business => Inner.Organization; public string Digest => Inner.Connector.PackageVersion!.PackageDigest!;
        public Secrets Secrets { get; } = new(); public Profiles Profiles { get; } = new(); public IdentityHttp Http { get; } = new(); public DiscoveryTransport Transport { get; } = new();
        private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();
        public PluginAccountService Service => new(Inner.Db, Secrets, protection, Http, Profiles, Transport,
            Options.Create(new PluginConnectionOptions { PublicBaseUrl = "https://platform.example" }), new TestAuditEventWriter());
        public Task<BeginPluginAccountResponse> Begin(string[]? scopes = null, Guid? reconnect = null) => Service.BeginAsync(Admin, Id, new(scopes ?? ["base"], "v1", true, Digest, reconnect), default);
        public Task<(Guid InstallationId, Guid SetupId)> Complete(BeginPluginAccountResponse begin) => Service.CompleteAsync(Admin, "provider-code", State(begin), null, default);
        public ValueTask DisposeAsync() { Http.Dispose(); return Inner.DisposeAsync(); }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture { Inner = await ConnectorPlanServiceTests.Fixture.Create() }; var db = f.Inner.Db; var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Inner.Connector.PackageVersion!.ManifestJson, json)!;
            var input = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false,"properties":{"pageToken":{"type":"string","maxLength":2048}}}""");
            var empty = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","additionalProperties":false,"properties":{}}""");
            var discover = manifest.ProviderOperations[0] with { Capability = "example.api.discover.v1", InputSchema = input,
                Http = new() { Connection = "account", ScopeSets = ["base"], Bootstrap = true, Endpoint = "https://api.example.com/resources", QueryInputs = new Dictionary<string,string> { ["pageToken"] = "/pageToken" } } };
            var identity = discover with { Capability = "example.api.identity.v1", InputSchema = empty, Http = discover.Http with { Endpoint = "https://api.example.com/identity", QueryInputs = new Dictionary<string,string>() } };
            var optional = manifest.ProviderOperations[0] with { Capability = "example.api.optional.v1", Http = manifest.ProviderOperations[0].Http! with { ScopeSets = ["optional"] } };
            manifest = manifest with { Protocol = new() { MinimumVersion = "2.4", MaximumVersion = "2.x" }, Runtime = manifest.Runtime with { SupportsMultipleInstallations = false },
                Connections = [manifest.Connections[0] with { ScopeSets = [new() { Id = "base", Required = true, Label = "Read", Scopes = ["read", "identity"] }, new() { Id = "optional", Label = "Optional", Scopes = ["optional"] }] }],
                ProviderOperations = [manifest.ProviderOperations[0], discover, identity, optional],
                Provides = [manifest.Provides[0], manifest.Provides[0] with { Name = discover.Capability, InputSchema = input, RiskClass = "bootstrap" },
                    manifest.Provides[0] with { Name = identity.Capability, InputSchema = empty, RiskClass = "bootstrap" }, manifest.Provides[0] with { Name = optional.Capability }],
                Setup = new() { Required = false, EntryFlow = "connect", Flows = [new() { Id = "connect", Steps = [new() { Id = "consent", Kind = "oauth-connect", Connection = "account", ScopeSet = "base" }] }] },
                Accounts = new() { Connection = "account", SetupFlow = "connect", ResourceType = "example.workspace", ResourceLabel = "Workspace", ResourcesLabel = "Workspaces",
                    DiscoveryCapability = discover.Capability, PageTokenInput = "pageToken", ResourceProjection = new() { ItemsPointer = "/items", IdPointer = "/id", NamePointer = "/name", NextPageTokenPointer = "/next" },
                    Identity = new() { Capability = identity.Capability, SubjectPointer = "/sub", NamePointer = "/name" },
                    PrivacyPolicyUrl = "https://example.com/privacy", TermsOfServiceUrl = "https://example.com/terms", PolicyVersion = "v1", ConsentPurpose = "Connect account and select business access.",
                    Permissions = [new() { Id = "read", Label = "Read workspaces", Purpose = "Read information", ScopeSets = ["base"], Capabilities = [ConnectorPlanServiceTests.Fixture.Capability] },
                        new() { Id = "optional", Label = "Optional ability", Purpose = "Optional scoped read", ScopeSets = ["optional"], Capabilities = [optional.Capability] }] } };
            f.Inner.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, json); f.Inner.Connector.PackageVersion.Status = AgentPackageVersionStatus.Built;
            f.Inner.Connector.Scope = PluginInstallationScope.System; f.Inner.Connector.BusinessId = "enterprise";
            f.Inner.Connector.Grant!.ProvidedCapabilitiesJson = JsonSerializer.Serialize(manifest.Provides.Select(x => x.Name));
            var role = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = AuthenticationService.AdministratorRole };
            db.Roles.Add(role); db.Users.AddRange(new ApplicationUser { Id = f.Admin, UserName = "admin" }, new ApplicationUser { Id = f.Manager, UserName = "manager" });
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = f.Admin, RoleId = role.Id });
            db.CoreOrganizations.Add(new Organization { Id = f.Business, Name = "Business A" });
            db.CoreOrganizationUsers.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Business, ApplicationUserId = f.Manager,
                DisplayName = "Manager", IsActive = true, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager });
            await db.SaveChangesAsync(); return f;
        }
    }
    internal sealed class Secrets : IPluginSecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task SetAsync(Guid id, string key, string value, CancellationToken ct = default) { Values[$"{id:N}:{key}"] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(Guid id, string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault($"{id:N}:{key}"));
        public Task RemoveAsync(Guid id, string key, CancellationToken ct = default) { Values.Remove($"{id:N}:{key}"); return Task.CompletedTask; }
    }
    internal sealed class Profiles : IPluginProviderProfileRegistry
    {
        public string ClientId { get; set; } = "test-client";
        public Task<PluginOAuthProviderProfile?> ResolveAsync(string id, CancellationToken ct = default) => Task.FromResult<PluginOAuthProviderProfile?>(new(id,"Example","https://identity.example.com/authorize","https://identity.example.com/token","https://identity.example.com/revoke",ClientId,"secret-stays-in-host"));
        public Task<IReadOnlyList<PluginProviderProfileResponse>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PluginProviderProfileResponse> UpsertAsync(string id, UpsertPluginProviderProfileRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
    internal sealed class IdentityHttp : HttpMessageHandler, IHttpClientFactory
    {
        public string Scopes { get; set; } = "read identity optional"; public string? RefreshToken { get; set; } = "host-only-refresh"; public int Exchanges { get; private set; }
        public Action? BeforeResponse { get; set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Exchanges++; BeforeResponse?.Invoke(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { access_token = "host-only-token", refresh_token = RefreshToken, token_type = "Bearer", scope = Scopes, expires_in = 3600 })) }); }
    }
    internal sealed class DiscoveryTransport : IConnectorHttpTransport
    {
        public string Subject { get; set; } = "immutable-provider-subject"; public bool FailSecondPage { get; set; }
        public int Pages { get; private set; } public int FirstPageRequests { get; private set; }
        public async Task<ConnectorProviderResponse> SendAsync(Guid connector, Guid connection, ConnectorPreparedRequest request, Func<CancellationToken,Task> revalidate, CancellationToken ct)
        {
            await revalidate(ct);
            if (request.Url.Contains("/identity")) return new(200, JsonSerializer.SerializeToUtf8Bytes(new { sub = Subject, name = "Example account" }));
            Pages++; var second = request.Url.Contains("pageToken=page-two"); if (!second) FirstPageRequests++;
            if (second && FailSecondPage) throw new HttpRequestException("simulated interruption");
            return new(200, Encoding.UTF8.GetBytes(second ? """{"items":[{"id":"workspace-b","name":"Workspace B"}]}""" : """{"items":[{"id":"workspace-a","name":"Workspace A"}],"next":"page-two"}"""));
        }
    }
}
