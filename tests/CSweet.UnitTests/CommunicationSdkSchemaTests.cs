using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public sealed class CommunicationSdkSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SdkProjectDecisionAcceptsOptionalTypeData(bool populated)
    {
        var request = new CSweet.WorkManagement.Contracts.DecisionRequest(Guid.NewGuid(), "game.toolchain",
            "Choose the project toolchain", "creative-owner", [new("a", "A", null), new("b", "B", null)],
            "a", [], null, "Technical planning awaits the choice.", null, "toolchain-decision",
            populated ? JsonSerializer.SerializeToElement(new { engine = "Godot" }) : null);
        Validate(CSweet.WorkManagement.Contracts.DecisionCapabilityNames.RequestV1,
            JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    private static void Validate(string capability, JsonElement payload)
    {
        var tool = Assert.Single(new McpToolCatalog([]).List(new HashSet<string> { capability }));
        JsonSchemaValidator.Validate(payload, tool.InputSchema);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedChatCreationAcceptsNullAndAssignedProjectContext(bool scoped)
    {
        var request = new CreateCommunicationChat(null, null, true, true, [Guid.NewGuid()])
        {
            WorkstreamId = scoped ? Guid.NewGuid() : null,
            TeamId = scoped ? Guid.NewGuid() : null
        };
        Validate("communication.chat.create.v1", JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task SdkDirectAgentMessagePassesHostValidationForChatCreationAndSending()
    {
        var director = Guid.NewGuid(); var chat = Guid.NewGuid(); var sends = 0;
        var runtime = new AgentTestRuntime()
            .RegisterCapability<JsonElement, JsonElement>("communication.chat.create.v1", (r, _) => {
                Validate("communication.chat.create.v1", r);
                return Task.FromResult(JsonSerializer.SerializeToElement(new { succeeded = true, message = "Created", chat = new {
                    id = chat, isDirect = true, isPrivate = true, participants = new[] {
                        new { organizationUserId = director, employeeType = "Agent", displayName = "Director", role = "Member" } } } }));
            })
            .RegisterCapability<JsonElement, CommunicationMessage>("communication.message.send.v1", (r, _) => {
                Validate("communication.message.send.v1", r); sends++;
                return Task.FromResult(new CommunicationMessage(Guid.NewGuid(), 1, chat, Guid.NewGuid(), "Producer", "Agent",
                    r.GetProperty("content").GetString()!, DateTimeOffset.UtcNow, Guid.NewGuid(), null, []));
            });
        await runtime.CreateContext().Platform.Communication.SendDirectAgentMessageAsync(director,
            "Please share the accepted pitch and GDD.", "producer-kickoff-schema-test", default);
        Assert.Equal(1, sends);
    }
}