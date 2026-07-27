namespace CSweet.AgentHost.Broker;

/// <summary>
/// Server-resolved identity and authority for one authenticated MCP request. This is not
/// agent-supplied state and contains no transport credentials.
/// </summary>
public sealed record AgentSession(
    string SessionId,
    string AgentId,
    string InstallationId,
    string BusinessId,
    string RuntimeInstanceId,
    string TickId,
    AuthorizedAgentGrant Grant,
    string? AgentVersion = null)
{
    public string? MemoryTenantId { get; set; }
    public string? MemoryEmployeeId { get; set; }
}

public sealed record AuthorizedAgentGrant(
    IReadOnlySet<string> ProvidedCapabilities,
    IReadOnlySet<string> Subscriptions,
    IReadOnlySet<string> RequiredCapabilities,
    long Revision)
{
    public IReadOnlySet<string> Capabilities => ProvidedCapabilities;
    public IReadOnlySet<string> RequestedCapabilities => RequiredCapabilities;
}
