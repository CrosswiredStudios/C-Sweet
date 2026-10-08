using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed partial class PlatformMemoryCapabilityHandlerTests
{
    [Fact]
    public Task SqliteAlignedNamespacesPreservePrivateHistory() => AlignedNamespacesContract(_store);

    [MemoryPostgresFact]
    public async Task PostgreSqlAlignedNamespacesPreservePrivateHistory()
    {
        await using var store = new PostgreSqlMemoryStore(Environment.GetEnvironmentVariable("CSWEET_MEMORY_TEST_POSTGRES")!);
        await AlignedNamespacesContract(store);
    }

    private async Task AlignedNamespacesContract(IMemoryStore store)
    {
        await store.InitializeAsync();
        var tenant = _organization.ToString("D");
        var employee = _employee.ToString("D");
        var scopes = new[] {
            EmployeeMemoryNamespaces.UserRelationship(tenant, employee, _user.ToString("D"), "csweet"),
            EmployeeMemoryNamespaces.Employee(tenant, employee, "csweet"),
            EmployeeMemoryNamespaces.Organization(tenant, "csweet")
        };
        var legacy = scopes.Select(x => x with { Partition = x.Partition with {
            ApplicationId = _installation.ToString("D") } }).ToArray();
        var handler = new PlatformMemoryCapabilityHandler(store, NullLogger<PlatformMemoryCapabilityHandler>.Instance,
            new AgentMemoryIdentityResolver(_db), new QueryContractEvidence());
        var catalog = new McpToolCatalog([new MemoryPlatformCapabilityAdapter(handler)]);
        var tools = catalog.List(new HashSet<string> { CSweetMemoryCapabilities.Query, CSweetMemoryCapabilities.Write });
        Assert.Equal(2, tools.Count);
        Assert.All(tools, tool => Assert.False(tool.ModelVisible));
        var queryTool = Assert.Single(tools, tool => tool.Capability == CSweetMemoryCapabilities.Query);
        Assert.Empty(catalog.List(new HashSet<string>()));
        JsonSchemaValidator.ValidateSchema(queryTool.OutputSchema!.Value);
        var peerInstallation = Guid.NewGuid();
        var peerEmployee = Guid.NewGuid();
        _db.AgentInstallations.Add(new AgentInstallation { Id = peerInstallation, BusinessId = tenant, IsEnabled = true });
        _db.CoreOrganizationUsers.Add(new OrganizationUser { Id = peerEmployee, OrganizationId = _organization,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = peerInstallation });
        await _db.SaveChangesAsync();
        var peer = new AgentSession("peer", "agent", peerInstallation.ToString("D"), tenant, "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { CSweetMemoryCapabilities.Query }, 1));
        var now = DateTimeOffset.UtcNow;
        try
        {
            foreach (var scope in scopes.Concat(legacy))
            {
                var text = scope.Partition.ApplicationId == "csweet" ? "shared-path memory" : "installation-private memory";
                await store.AppendEpisodeAsync(new MemoryEpisode(Guid.NewGuid(), scope.Partition, scope.Scope, text,
                    "text/plain", new("trusted-fixture", "source"), "checksum", now.AddMinutes(-1), now,
                    Sensitivity: scope.Scope == MemoryScope.Tenant ? MemorySensitivity.Internal : MemorySensitivity.Personal));
            }
            foreach (var scope in scopes)
            {
                var response = await Search(Session(), scope);
                Assert.True(response.Succeeded, response.Error);
                Assert.Equal("shared-path memory", Assert.Single(Items(response)).Content);
                // Search must survive the actual MCP output validator, not just the handler.
                McpGatewayEndpoints.ValidateSuccessfulToolOutput(true,
                    JsonSerializer.Deserialize<JsonElement>(response.Payload.Span), queryTool.OutputSchema.Value);
            }
            // Legacy clients keep their exact private routes; no automatic union or rewrite.
            foreach (var scope in legacy)
            {
                var response = await Search(Session(), scope);
                Assert.True(response.Succeeded, response.Error);
                Assert.Equal("installation-private memory", Assert.Single(Items(response)).Content);
                Assert.False((await Search(peer, scope)).Succeeded);
            }
            Assert.True((await Search(peer, scopes[2])).Succeeded);
            Assert.False((await Search(peer, scopes[0])).Succeeded);
            Assert.False((await Search(peer, scopes[1])).Succeeded);

            // New agent proposals in the aligned business route still cannot publish private evidence.
            var proposal = new MemoryEpisode(Guid.NewGuid(), scopes[2].Partition, MemoryScope.Tenant,
                "unreviewed memory", "text/plain", new("user", "forged"), "checksum", now, now,
                Sensitivity: MemorySensitivity.Public);
            var write = await handler.HandleAsync(Session(), Request(CSweetMemoryCapabilities.Write,
                "append-episode", proposal), default);
            Assert.True(write.Succeeded, write.Error);
            var id = JsonSerializer.Deserialize<MemoryWriteResult>(write.Payload.Span, JsonOptions)!.Id;
            Assert.Equal(MemorySensitivity.Personal,
                (await ((IMemorySourceReader)store).GetEpisodeAsync(scopes[2].Partition, id))!.Sensitivity);
            Assert.Equal("shared-path memory", Assert.Single(Items(await Search(peer, scopes[2]))).Content);

            // Existing grants do not silently gain the corrected capability declarations.
            var oldGrant = new AgentSession("old", "agent", _installation.ToString("D"), tenant, "runtime", "tick",
                new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "memory.user.read.v1", "memory.business.read.v1" }, 1));
            Assert.False((await Search(oldGrant, scopes[0])).Succeeded);

            (await _db.CoreOrganizationUsers.SingleAsync(x => x.Id == _user)).IsActive = false;
            await _db.SaveChangesAsync();
            Assert.False((await Search(Session(), scopes[0])).Succeeded);
            Assert.True((await Search(Session(), scopes[1])).Succeeded);
            (await _db.AgentInstallations.SingleAsync(x => x.Id == _installation)).IsEnabled = false;
            await _db.SaveChangesAsync();
            Assert.False((await Search(Session(), scopes[2])).Succeeded);
        }
        finally
        {
            foreach (var scope in scopes.Concat(legacy)) await store.DeleteScopeAsync(scope.Partition);
        }

        Task<CapabilityResult> Search(AgentSession session, MemoryNamespace scope) => handler.HandleAsync(session,
            Request(CSweetMemoryCapabilities.Query, "search", new MemorySearchRequest(scope.Partition, scope.Scope, "memory")), default);
        static MemoryCandidate[] Items(CapabilityResult response) =>
            JsonSerializer.Deserialize<MemoryCandidate[]>(response.Payload.Span, JsonOptions)!;
    }
}
