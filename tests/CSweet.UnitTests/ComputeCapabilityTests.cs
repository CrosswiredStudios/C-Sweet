using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.AgentHost.Broker;
using CSweet.Compute.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ComputeCapabilityTests
{
    private static readonly HashSet<string> Capabilities = [InfrastructureActions.Provision, InfrastructureActions.Read,
        InfrastructureActions.List, InfrastructureActions.Start, InfrastructureActions.Stop, InfrastructureActions.Restart, InfrastructureActions.Destroy];

    [Fact]
    public async Task Authenticated_tools_request_reuse_read_list_and_destroy_through_the_real_broker()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var handler = new ComputeCapabilityHandler(f.Broker);
        var session = Session(f);
        var input = ProvisionInput(f);
        var tool = new McpToolCatalog([handler]).Find("request_compute_environment", Capabilities)!;
        JsonSchemaValidator.Validate(input, tool.InputSchema);
        var first = await Invoke(handler, session, InfrastructureActions.Provision, input); Assert.True(first.Succeeded, first.Error);
        var id = first.Payload.ToElement().GetProperty("id").GetGuid();
        Assert.Equal(id, (await Invoke(handler, session, InfrastructureActions.Provision, input)).Payload.ToElement().GetProperty("id").GetGuid());
        var read = await Invoke(handler, session, InfrastructureActions.Read, JsonSerializer.SerializeToElement(new { environmentId = id }));
        Assert.True(read.Succeeded, read.Error); Assert.DoesNotContain("test-provider", read.Payload.ToStringUtf8());
        var list = await Invoke(handler, session, InfrastructureActions.List, JsonSerializer.SerializeToElement(new { workstreamId = f.Workstream, limit = 10 }));
        Assert.True(list.Succeeded, list.Error); Assert.Equal(1, list.Payload.ToElement().GetProperty("items").GetArrayLength());
        var destroy = await Invoke(handler, session, InfrastructureActions.Destroy,
            JsonSerializer.SerializeToElement(new { environmentId = id, expectedGeneration = 1, idempotencyKey = "destroy-1" }));
        Assert.True(destroy.Succeeded, destroy.Error);
        Assert.Equal(2, destroy.Payload.ToElement().GetProperty("generation").GetInt64());
        Assert.Single(await f.Db.ComputeEnvironments.ToListAsync());
        Assert.Equal(f.Installation, (await f.Db.ComputeEnvironments.SingleAsync()).InstallationId);
    }

    [Fact]
    public async Task Caller_scope_or_action_fields_are_rejected_instead_of_overriding_session_or_tool_authority()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var handler = new ComputeCapabilityHandler(f.Broker);
        var input = JsonNode.Parse(ProvisionInput(f).GetRawText())!;
        input["installationId"] = Guid.NewGuid(); input["organizationId"] = Guid.NewGuid();
        Assert.Equal("compute_request_invalid", (await Invoke(handler, Session(f), InfrastructureActions.Provision,
            JsonSerializer.SerializeToElement(input))).FailureCode);
        var lifecycle = JsonSerializer.SerializeToElement(new { environmentId = Guid.NewGuid(), expectedGeneration = 1,
            idempotencyKey = "key", action = InfrastructureActions.Destroy });
        Assert.Equal("compute_request_invalid", (await Invoke(handler, Session(f), InfrastructureActions.Start, lifecycle)).FailureCode);
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
        Assert.Empty(await f.Db.ComputeOperations.ToListAsync());
    }

    [Fact]
    public async Task Revoked_database_authority_or_ungranted_session_cannot_be_replaced_by_request_identity()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var handler = new ComputeCapabilityHandler(f.Broker);
        var session = Session(f);
        var ungranted = session with { Grant = new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), 1) };
        Assert.False((await Invoke(handler, ungranted, InfrastructureActions.Provision, ProvisionInput(f))).Succeeded);
        (await f.Db.ScopedActionGrants.SingleAsync(x => x.Action == InfrastructureActions.Provision)).RevokedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        Assert.Equal("compute_authority_denied", (await Invoke(handler, session, InfrastructureActions.Provision, ProvisionInput(f))).FailureCode);
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
    }

    [Fact]
    public async Task Compute_provision_does_not_implicitly_authorize_network_access()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var input = JsonNode.Parse(ProvisionInput(f).GetRawText())!;
        input["specification"]!["network"] = new JsonObject { ["mode"] = "outboundOnly", ["allowOutbound"] = true };
        var result = await Invoke(new(f.Broker), Session(f), InfrastructureActions.Provision, JsonSerializer.SerializeToElement(input));
        Assert.Equal("compute_authority_denied", result.FailureCode);
        Assert.Empty(await f.Db.ComputeEnvironments.ToListAsync());
    }

    [Fact]
    public void Catalog_exposes_only_requested_capabilities_with_closed_input_schemas()
    {
        var catalog = new McpToolCatalog([]);
        Assert.Empty(catalog.List(new HashSet<string>()));
        var tools = catalog.List(Capabilities); Assert.Equal(7, tools.Count);
        foreach (var tool in tools)
        {
            JsonSchemaValidator.ValidateSchema(tool.InputSchema);
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(32768, tool.MaximumInputBytes); Assert.Equal("compute", tool.OwningService);
        }
        Assert.Single(catalog.List(new HashSet<string> { InfrastructureActions.Read }));
    }

    [Fact]
    public async Task Defaults_selector_returns_the_authenticated_installations_preparation_state()
    {
        await using var f = new ComputeBrokerTests.Fixture(); await f.SeedAsync();
        var defaults = new CSweet.Infrastructure.Compute.ComputeDefaultsService(f.Db, TimeProvider.System,
            new CSweet.Infrastructure.Compute.ComputeGrantAdministration(f.Db, TimeProvider.System, new CSweet.Infrastructure.Setup.AuditExecutionContextAccessor()));
        await defaults.EnsureRequestedAsync(f.Installation, default);
        var handler = new ComputeCapabilityHandler(f.Broker, defaults);
        var input = JsonSerializer.SerializeToElement(new { defaults = true });
        var tool = new McpToolCatalog([handler]).List(Capabilities).Single(x => x.Capability == InfrastructureActions.Read);
        JsonSchemaValidator.Validate(input, tool.InputSchema);
        var result = await Invoke(handler, Session(f), InfrastructureActions.Read, input);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Pending", result.Payload.ToElement().GetProperty("state").GetString());
        Assert.Equal("compute_request_invalid", (await Invoke(handler, Session(f), InfrastructureActions.Read,
            JsonSerializer.SerializeToElement(new { defaults = true, environmentId = Guid.NewGuid() }))).FailureCode);
    }
    private static AgentSession Session(ComputeBrokerTests.Fixture f) => new("session", "compute-agent", f.Installation.ToString("D"),
        f.Organization.ToString("D"), "runtime", "tick", new(new HashSet<string>(), new HashSet<string>(), Capabilities, 1));

    private static JsonElement ProvisionInput(ComputeBrokerTests.Fixture f) => JsonSerializer.SerializeToElement(new
    {
        workstreamId = f.Workstream, desiredEnvironmentKey = f.Request.DesiredEnvironmentKey, idempotencyKey = f.Request.IdempotencyKey,
        specification = new { operatingSystem = "linux", architecture = "x64", templateId = "ubuntu-clean",
            resources = new { cpuCount = 2, memoryMiB = 4096, diskMiB = 20480 }, lifetimeSeconds = 600 }
    });

    private static async Task<CapabilityResult> Invoke(ComputeCapabilityHandler handler, AgentSession session, string action, JsonElement input)
    {
        var result = new List<CapabilityResult>();
        await foreach (var item in new PlatformCapabilityDispatcher([handler]).InvokeAsync(session,
            new() { RequestId = "request", RequestingAgentId = "untrusted-payload-identity", Capability = action,
                Payload = JsonPayload.FromUtf8(input.GetRawText()) }, default)) result.Add(item);
        return Assert.Single(result);
    }
}
