namespace CSweet.Domain.Setup;

/// <summary>Enterprise-scoped credential retirement. Never stores credential values or a fake business identity.</summary>
public sealed class PluginAccountRevocationJob
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public Guid AccountGroupId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileHash { get; set; } = string.Empty;
    public string CredentialKey { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public DateTimeOffset Deadline { get; set; }
    public DateTimeOffset NextAttempt { get; set; }
    public int Attempts { get; set; }
    public long Revision { get; set; } = 1;
}
