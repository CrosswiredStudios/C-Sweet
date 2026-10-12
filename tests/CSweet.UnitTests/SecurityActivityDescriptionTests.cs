using CSweet.AgentHost.Broker;
using CSweet.Contracts.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Security;
using System.Text.Json;

namespace CSweet.UnitTests;

public sealed class SecurityActivityDescriptionTests
{
    [Theory]
    [InlineData("agent.capability.started", null)]
    [InlineData("agent.capability.executed", "{\"toolName\":\"read_resource_changes\"}")]
    [InlineData("agent.capability.started", "{\"capability\":\"platform.management.resource-change.read.v1\"}")]
    [InlineData("agent.capability.started", "invalid json")]
    public void HistoricalActionRecordsUseShortDescriptions(string eventType, string? metadata)
    {
        var item = new AuditEvent { EventType = eventType, MetadataJson = metadata,
            Summary = "com.csweet.video-game-creative-director invoked read_resource_changes (platform.management.resource-change.read.v1)." };
        Assert.Equal("Read team change requests", AuditActivityDescriptions.Describe(item, false));
    }

    [Fact]
    public void CapturedActionDescriptionSurvivesToolRenamingAndPreservesFailureOutcome()
    {
        var item = new AuditEvent { EventType = "agent.capability.executed", Outcome = "Failed",
            MetadataJson = "{\"activityDescription\":\"Read team change requests\",\"toolName\":\"renamed_tool\"}" };
        Assert.Equal("Read team change requests", AuditActivityDescriptions.Describe(item, false));
        Assert.Equal("Failed", item.Outcome);
    }

    [Theory]
    [InlineData("security.timeline.viewed", "Opened security audit history")]
    [InlineData("artifact.access.granted", "Granted document access")]
    [InlineData("model.response.chunk", "Received part of a model response")]
    [InlineData("agent.work.failed", "Work failed")]
    public void CommonEventsHaveReadableDescriptions(string eventType, string expected)
    {
        Assert.Equal(expected, AuditActivityDescriptions.Describe(new AuditEvent {
            EventType = eventType, Outcome = eventType == "agent.work.failed" ? "Failed" : "Completed", Summary = "A human manager opened security audit data." }, false));
    }

    [Fact]
    public void DeniedSecurityAccessDoesNotClaimTheDataWasOpened()
    {
        Assert.Equal("Attempted to open security audit data", AuditActivityDescriptions.Describe(new AuditEvent {
            EventType = "security.timeline.viewed", Outcome = "Denied" }, false));
    }

    [Fact]
    public void UnknownActionsAndEventsHaveReadableFallbacks()
    {
        Assert.Equal("Review release notes", AuditActionDescriptions.ForTool("review_release_notes"));
        Assert.Equal("Used a platform action", AuditActivityDescriptions.Describe(new AuditEvent {
            EventType = "agent.capability.started", MetadataJson = "[]" }, false));
        Assert.Equal("Reviewed the sprint plan.", AuditActivityDescriptions.Describe(new AuditEvent {
            EventType = "custom.reviewed", ActorDisplayName = "Ada", Summary = "Ada reviewed the sprint plan." }, false));
        Assert.Equal("Custom review recorded", AuditActivityDescriptions.Describe(new AuditEvent {
            EventType = "custom.review.recorded" }, false));
    }

    [Fact]
    public void ToolDescriptorExposesShortAuditLabelSeparateFromToolInstructions()
    {
        var descriptor = new McpToolDescriptor("platform.management.resource-change.read.v1", "read_resource_changes",
            "Read resource-change requests visible to this requester, assigned manager, or active Chief of Staff.",
            JsonSerializer.SerializeToElement(new { }), null, McpToolExecutionPolicy.ReadOnly);
        Assert.Equal("Read team change requests", descriptor.ActivityDescription);
        Assert.Contains("Chief of Staff", descriptor.Description);
    }
}
