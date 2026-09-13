using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Compute.Contracts;
using Sdk = CSweet.Agent.SDK.Compute;

namespace CSweet.UnitTests;

public sealed class ComputeSdkContractTests
{
    [Fact]
    public async Task Typed_sdk_provisions_reads_lists_and_destroys_through_the_current_broker_and_MCP_schemas()
    {
        await using var fixture = new ComputeBrokerTests.Fixture(); await fixture.SeedAsync();
        var handler = new ComputeCapabilityHandler(fixture.Broker);
        var capabilities = new HashSet<string> { Sdk.ComputeCapabilities.Provision, Sdk.ComputeCapabilities.Read,
            Sdk.ComputeCapabilities.List, Sdk.ComputeCapabilities.Destroy };
        var session = new AgentSession("session", "compute-agent", fixture.Installation.ToString("D"),
            fixture.Organization.ToString("D"), "runtime", "tick", new(new HashSet<string>(), new HashSet<string>(), capabilities, 1));
        var runtime = new AgentTestRuntime();
        foreach (var capability in capabilities)
        {
            runtime.RegisterCapability<JsonElement, JsonElement>(capability, async (input, token) => {
                var schema = ComputeMcpTools.All.Single(x => x.Capability == capability).InputSchema;
                JsonSchemaValidator.Validate(input, schema);
                CapabilityResult? response = null;
                await foreach (var result in handler.HandleAsync(session, new() {
                    RequestId = "sdk-test", Capability = capability, Payload = JsonPayload.FromUtf8(input.GetRawText())
                }, token)) response = result;
                Assert.NotNull(response); Assert.True(response.Succeeded, response.Error);
                return response.Payload.ToElement();
            });
        }
        var compute = runtime.CreateContext().Platform.Compute;
        var request = new Sdk.ProvisionComputeRequest(fixture.Workstream, "sdk-environment", "sdk-provision",
            new("linux", "x64", "ubuntu-clean", new(2, 4096, 20480), 600));
        var environment = await compute.ProvisionAsync(request);
        Assert.Equal(environment.Id, (await compute.ProvisionAsync(request)).Id);
        Assert.Equal(environment.Id, (await compute.ReadAsync(environment.Id)).Id);
        Assert.Equal(environment.Id, Assert.Single((await compute.ListAsync(fixture.Workstream)).Items).Id);
        var destroyed = await compute.DestroyAsync(new(environment.Id, environment.Generation, "sdk-destroy"));
        Assert.Equal(environment.Generation + 1, destroyed.Generation);
    }

    [Fact]
    public async Task Typed_workloads_match_strict_Core_contracts_and_keep_guest_protocol_fields_private()
    {
        var id = Guid.NewGuid();
        var runtime = new AgentTestRuntime();
        foreach (var capability in new[] { Sdk.ComputeCapabilities.Execute, Sdk.ComputeCapabilities.PublishPort })
        {
            runtime.RegisterCapability<JsonElement, JsonElement>(capability, (input, _) => {
                JsonSchemaValidator.Validate(input, ComputeMcpTools.All.Single(x => x.Capability == capability).InputSchema);
                var request = input.Deserialize<CSweet.Application.Compute.RequestComputeWorkload>(ComputeProtocol.Json)!;
                var workload = request.Workload.Validate("linux");
                Assert.Equal(capability == Sdk.ComputeCapabilities.Execute, workload.Command is not null);
                var result = workload.Command is { } command
                    ? new ComputeWorkloadResult(Command: new(1, Guid.NewGuid(), command.RequestId, 0, false, "hello"u8.ToArray(), [], false))
                    : new ComputeWorkloadResult(Url: "http://127.0.0.1:45678/", UrlExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
                return Task.FromResult(JsonSerializer.SerializeToElement(new CSweet.Application.Compute.ComputeOperationView(
                    Guid.NewGuid(), id, request.ExpectedGeneration + 1, "Completed", null, result), ComputeProtocol.Json));
            });
        }
        var compute = runtime.CreateContext().Platform.Compute;
        var executed = await compute.ExecuteAsync(new(id, 1, "sdk-command", new(Guid.NewGuid(), "/bin/echo", "/", ["hello"])));
        Assert.Equal("hello", executed.Result!.Command!.StandardOutputText);
        Assert.Equal("http://127.0.0.1:45678/", (await compute.PublishPortAsync(new(id, 2, "sdk-publish", 8080))).Result!.Url);
        Assert.DoesNotContain(typeof(Sdk.ComputeCommandResult).GetProperties(), p => p.Name is "Challenge" or "Version");
    }
}
