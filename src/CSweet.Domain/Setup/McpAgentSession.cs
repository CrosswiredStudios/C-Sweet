namespace CSweet.Domain.Setup;

public sealed class McpAgentSession
{
    public Guid Id { get; set; }
    public Guid RuntimeInstanceId { get; set; }
    public Guid TickId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string OrganizationId { get; set; } = string.Empty;
    public Guid PackageVersionId { get; set; }
    public string PackageDigest { get; set; } = string.Empty;
    public long GrantRevision { get; set; }
    public string AccessTokenHash { get; set; } = string.Empty;
    public string? PreviousAccessTokenHash { get; set; }
    public DateTimeOffset? PreviousTokenValidUntil { get; set; }
    public DateTimeOffset EstablishedAt { get; set; }
    public DateTimeOffset LastRenewedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }

    public AgentRuntimeInstance? RuntimeInstance { get; set; }
    public AgentInstallation? AgentInstallation { get; set; }
}
