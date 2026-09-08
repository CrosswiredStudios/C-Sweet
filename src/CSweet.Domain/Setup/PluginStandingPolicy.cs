namespace CSweet.Domain.Setup;

// Historical records only. No runtime authorizer may consume these provider-shaped policies.
// Keep storage and disconnect/account-change revocation until explicit data retirement;
// new connector standing policies must bind reviewed operations and frozen request plans.
public sealed class PluginStandingPolicy
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid AgentInstallationId { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string PolicyJson { get; set; } = "{}";
    public string PayloadHash { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public PluginStandingPolicyStatus Status { get; set; }
    public Guid ApprovedByOrganizationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum PluginStandingPolicyStatus
{
    Approved,
    Revoked
}
