using System.Text;
using System.Text.Json;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ConnectorBootstrapTests
{
    [Fact]
    public async Task AuthenticatedDiscoveryReturnsOnlyNativeAccountFields()
    {
        await using var fixture = await Create();
        var transport = new FakeTransport("""{"items":[{"id":"account-a","label":"Provider name","unexpected":"not forwarded"}]}""");
        var result = await new ConnectorBootstrapExecutor(fixture.Db, transport).ExecuteAsync(
            fixture.Organization, fixture.Connector.Id, "select", Input(), default);
        Assert.Equal("Provider name", result.GetProperty("accounts")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("unexpected", result.GetRawText());
        Assert.Single(transport.Requests);
        Assert.Contains("mine=true", transport.Requests[0]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"items\":[{\"id\":\"a\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"label\":\"A\"},{\"id\":\"a\",\"label\":\"B\"}]}")]
    [InlineData("{\"items\":[],\"nextPage\":\"unfinished\"}")]
    public async Task MalformedAmbiguousOrIncompleteDiscoveryFailsClosed(string response)
    {
        await using var fixture = await Create();
        await Assert.ThrowsAnyAsync<Exception>(() => new ConnectorBootstrapExecutor(fixture.Db, new FakeTransport(response))
            .ExecuteAsync(fixture.Organization, fixture.Connector.Id, "select", Input(), default));
    }

    [Fact]
    public async Task TenantStepGrantAndConsentAreIndependentChecks()
    {
        await using var fixture = await Create();
        var transport = new FakeTransport("{\"items\":[]}");
        var executor = new ConnectorBootstrapExecutor(fixture.Db, transport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(Guid.NewGuid(), fixture.Connector.Id, "select", Input(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Organization, fixture.Connector.Id, "other-step", Input(), default));
        fixture.Connector.Grant!.ProvidedCapabilitiesJson = "[]";
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Organization, fixture.Connector.Id, "select", Input(), default));
        fixture.Connector.Grant.ProvidedCapabilitiesJson = JsonSerializer.Serialize(new[] { ConnectorPlanServiceTests.Fixture.Capability });
        fixture.Connection.GrantedScopesJson = "[]";
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(fixture.Organization, fixture.Connector.Id, "select", Input(), default));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RevocationDuringDiscoveryDiscardsTheResponse()
    {
        await using var fixture = await Create();
        var transport = new FakeTransport("{\"items\":[]}", async () =>
        {
            fixture.Connection.Status = PluginConnectionStatus.Revoked;
            await fixture.Db.SaveChangesAsync();
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new ConnectorBootstrapExecutor(fixture.Db, transport)
            .ExecuteAsync(fixture.Organization, fixture.Connector.Id, "select", Input(), default));
    }

    [Fact]
    public async Task ValidationRequiresTheConfirmedAccountInAuthenticatedDiscovery()
    {
        await using var fixture = await Create(health: true);
        fixture.Connection.BoundResourceId = "account-a";
        await fixture.Db.SaveChangesAsync();
        var valid = new ConnectorBootstrapExecutor(fixture.Db, new FakeTransport("""{"items":[{"id":"account-a","label":"A"}]}"""));
        Assert.True((await valid.ExecuteAsync(fixture.Organization, fixture.Connector.Id, "select", Input(), default)).GetProperty("healthy").GetBoolean());
        var invalid = new ConnectorBootstrapExecutor(fixture.Db, new FakeTransport("""{"items":[{"id":"account-b","label":"B"}]}"""));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => invalid.ExecuteAsync(fixture.Organization, fixture.Connector.Id, "select", Input(), default));
    }

    private static JsonElement Input() => JsonSerializer.SerializeToElement(new { });
    private static async Task<ConnectorPlanServiceTests.Fixture> Create(bool health = false)
    {
        var fixture = await ConnectorPlanServiceTests.Fixture.Create();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(fixture.Connector.PackageVersion!.ManifestJson, options)!;
        var input = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });
        manifest = manifest with
        {
            Provides = [manifest.Provides[0] with { RiskClass = "bootstrap", InputSchema = input }],
            ProviderOperations = [manifest.ProviderOperations[0] with { InputSchema = input, Http = new()
            { Connection = "account", ScopeSets = ["base"], Endpoint = "https://api.example.com/items", Bootstrap = true,
                QueryConstants = new Dictionary<string, string> { ["mine"] = "true" } } }],
            Setup = new() { Required = true, EntryFlow = "connect", Flows = [new() { Id = "connect", Steps = [new()
            { Id = "select", Kind = health ? "health-check" : "account-selector", Connection = "account",
                Capability = ConnectorPlanServiceTests.Fixture.Capability,
                AccountOptions = new() { ItemsPointer = "/items", IdPointer = "/id", NamePointer = "/label", NextPageTokenPointer = "/nextPage" } }] }] }
        };
        fixture.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, options);
        fixture.Connector.SetupState = PluginSetupState.NeedsSetup;
        fixture.Connector.SetupFlowId = "connect"; fixture.Connector.SetupStepId = "select";
        fixture.Connection.BoundResourceId = null;
        await fixture.Db.SaveChangesAsync(); return fixture;
    }
    private sealed class FakeTransport(string response, Func<Task>? afterRequest = null) : IConnectorHttpTransport
    {
        public List<string> Requests { get; } = [];
        public async Task<ConnectorProviderResponse> SendAsync(Guid connectorId, Guid connectionId,
            ConnectorPreparedRequest request, Func<CancellationToken, Task> revalidate, CancellationToken token)
        {
            await revalidate(token); Requests.Add(request.Url);
            if (afterRequest is not null) await afterRequest();
            return new(200, Encoding.UTF8.GetBytes(response));
        }
    }
}
