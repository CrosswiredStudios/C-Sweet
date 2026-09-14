namespace CSweet.AgentHost.Broker;

public sealed class McpGatewayOptions
{
    public const string SectionName = "CSweet:Mcp:Limits";

    public int MaximumRequestBytes { get; set; }
    public int MaximumInlineTextBytes { get; set; }
}
