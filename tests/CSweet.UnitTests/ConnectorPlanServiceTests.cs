using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ConnectorPlanServiceTests
{
    [Fact]
    public async Task StableKeyReusesPlanButRejectsModifiedInput()
    {
        await using var fixture = await Fixture.Create();
        var first = await fixture.Prepare("one");
        Assert.Equal(first.Id, (await fixture.Prepare("one")).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Prepare("changed"));
        Assert.Equal(1, await fixture.Db.ConnectorExecutions.CountAsync());
    }

    [Fact]
    public async Task CrossTenantCannotPrepareOrResumePlan()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Prepare("one");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.PrepareAsync(Guid.NewGuid(), fixture.Requester.Id,
            Fixture.Capability, Fixture.Input("one"), "other", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RevalidateAsync(Guid.NewGuid(), fixture.Requester.Id,
            plan.Id, plan.PlanHash, default));
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("consumer-grant")]
    [InlineData("provider-grant")]
    [InlineData("package")]
    [InlineData("channel")]
    [InlineData("profile")]
    public async Task ExecutionRechecksEveryAuthorityBoundary(string change)
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Prepare("one");
        switch (change)
        {
            case "disconnect": fixture.Connection.Status = PluginConnectionStatus.Revoked; break;
            case "consumer-grant": fixture.Requester.Grant!.GrantRevision++; break;
            case "provider-grant": fixture.Connector.Grant!.GrantRevision++; break;
            case "package": fixture.Connector.PackageVersion!.PackageDigest = new string('b', 64); break;
            case "channel": fixture.Connection.BoundResourceId = "different"; break;
            case "profile": (await fixture.Db.ConnectorProfileApprovals.SingleAsync()).RevokedAt = DateTimeOffset.UtcNow; break;
        }
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RevalidateAsync(fixture.Organization,
            fixture.Requester.Id, plan.Id, plan.PlanHash, default));
    }

    [Fact]
    public async Task StoredPlanTamperingFailsClosed()
    {
        await using var fixture = await Fixture.Create();
        var plan = await fixture.Prepare("one");
        plan.PlanJson = plan.PlanJson.Replace("api.example.com", "attacker.example.com", StringComparison.Ordinal);
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RevalidateAsync(fixture.Organization,
            fixture.Requester.Id, plan.Id, plan.PlanHash, default));
    }

    [Fact]
    public async Task IndeterminateOutcomeCannotBeRetried()
    {
        await using var fixture = await Fixture.Create(); var plan = await fixture.Prepare("one");
        plan.Status = "Indeterminate"; await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RevalidateAsync(fixture.Organization,
            fixture.Requester.Id, plan.Id, plan.PlanHash, default));
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public const string Capability = "example.api.read.v1";
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Organization { get; } = Guid.NewGuid();
        public AgentInstallation Requester { get; private set; } = null!;
        public AgentInstallation Connector { get; private set; } = null!;
        public PluginConnection Connection { get; private set; } = null!;
        public ConnectorPlanService Service => new(Db);
        public static JsonElement Input(string value) => JsonSerializer.SerializeToElement(new { search = value });
        public Task<ConnectorExecution> Prepare(string value) => Service.PrepareAsync(Organization, Requester.Id, Capability, Input(value), "stable", default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
        public static async Task<Fixture> Create(bool ownershipCheck = false)
        {
            var f = new Fixture(); var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"search":{"type":"string","maxLength":100}},"required":["search"],"additionalProperties":false}""");
            var output = JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");
            var connectorManifest = new PluginManifest
            {
                Id = "com.example.connector", Kind = "connector", Name = "Example", Version = "0.1.0",
                Protocol = new() { MinimumVersion = "2.1", MaximumVersion = "2.x" },
                Runtime = new() { SupportsMultipleInstallations = true },
                Provides = [new() { Name = Capability, InputSchema = schema, OutputSchema = output, Idempotency = "none" }],
                Connections = [new() { Id = "account", ProviderProfile = "example.profile", AllowedOrigins = ["https://api.example.com"],
                    ScopeSets = [new() { Id = "base", Scopes = ["read"] }],
                    Provider = new() { DisplayName = "Example", AuthorizationEndpoint = "https://identity.example.com/authorize",
                        TokenEndpoint = "https://identity.example.com/token", RevocationEndpoint = "https://identity.example.com/revoke" } }],
                ProviderOperations = [new() { Capability = Capability, Effect = "read", Idempotency = "none", InputSchema = schema, OutputSchema = output,
                    Http = new() { Connection = "account", ScopeSets = ["base"], Endpoint = "https://api.example.com/items",
                        BoundResourceQuery = "owner", QueryInputs = new Dictionary<string, string> { ["search"] = "/search" },
                        ResourceChecks = ownershipCheck ? [new() { Endpoint = "https://api.example.com/ownership",
                            InputPointer = "/search", OwnerPointer = "/owner" }] : [] } }]
            };
            var requesterManifest = new PluginManifest
            {
                Requires = [new() { Name = Capability, Dependency = "account" }],
                Dependencies = [new() { Id = "account", PluginId = "com.example.connector", PublisherId = "com.example",
                    MinimumVersion = "0.1.0", MaximumVersionExclusive = "0.2.0" }]
            };
            f.Requester = f.Install(requesterManifest, PluginKind.Agent, options);
            f.Connector = f.Install(connectorManifest, PluginKind.Connector, options);
            f.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { Capability });
            f.Connector.Grant!.ProvidedCapabilitiesJson = JsonSerializer.Serialize(new[] { Capability });
            f.Connection = new() { Id = Guid.NewGuid(), AgentInstallationId = f.Connector.Id, DeclarationId = "account",
                ProviderProfile = "example.profile", BoundResourceId = "confirmed", GrantedScopesJson = "[\"read\"]", Status = PluginConnectionStatus.Connected };
            f.Db.PluginConnections.Add(f.Connection);
            f.Db.AgentCapabilityBindings.Add(new() { Id = Guid.NewGuid(), RequesterInstallationId = f.Requester.Id,
                ProviderInstallationId = f.Connector.Id, OrganizationId = f.Organization.ToString("D"), Capability = Capability,
                DependencyId = "account", GrantRevision = 1, ProviderPackageDigest = f.Connector.PackageVersion!.PackageDigest });
            f.Db.ConnectorProfileApprovals.Add(new() { Id = Guid.NewGuid(), ConnectorInstallationId = f.Connector.Id,
                PackageDigest = f.Connector.PackageVersion.PackageDigest!, ProfileId = "example.profile" });
            await f.Db.SaveChangesAsync(); return f;
        }
        private AgentInstallation Install(PluginManifest manifest, PluginKind kind, JsonSerializerOptions options)
        {
            var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = Organization.ToString("D"),
                PackageVersion = new() { Id = Guid.NewGuid(), AgentId = manifest.Id, Version = "0.1.0", PublisherId = "com.example",
                    PluginKind = kind, PackageDigest = new string('a', 64), ManifestJson = JsonSerializer.Serialize(manifest, options) },
                Grant = new() { Id = Guid.NewGuid(), GrantRevision = 1 } };
            Db.AgentInstallations.Add(installation); return installation;
        }
    }
}
