using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;

namespace CSweet.UnitTests;

public static class DeliveryWorkflowBridge
{
    public static void ValidateReview(ReviewGitMergeRequest request)
    {
        var tool = new McpToolCatalog([]).List(new HashSet<string> { GitMergeCapabilities.Review }).Single();
        var result = McpGatewayEndpoints.ValidateToolInput("workflow-review", JsonSerializer.SerializeToElement(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        if (result is not null) throw new InvalidOperationException(result.Error);
    }
}
