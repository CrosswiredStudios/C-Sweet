using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class SprintSdkSchemaTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public void TypedSprintCreationAcceptsOptionalPositiveSequence(int? sequence)
    {
        Validate(sequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SprintCreationRejectsNonpositiveSequence(int sequence)
    {
        Assert.Throws<InvalidOperationException>(() => Validate(sequence));
    }

    private static void Validate(int? sequence)
    {
        var request = new CreateWorkSprintRequest(Guid.NewGuid(), "Production Sprint 1",
            "Draft scope", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14), "draft-sprint")
            { Sequence = sequence };
        var tool = Assert.Single(new McpToolCatalog([]).List(
            new HashSet<string> { WorkManagementCapabilityNames.SprintCreate }));
        JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
    }
}
