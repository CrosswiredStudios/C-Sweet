namespace CSweet.Domain.Setup;

/// <summary>An organization-scoped product host identity; never an employee or Office node identity.</summary>
public sealed class WebHostRegistration
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProviderInstallationId { get; set; }
    public Guid RegistrationRequestId { get; set; }
    public Guid RegisteredByOrganizationUserId { get; set; }
    public string RegistrationDigest { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string IdentityPublicKeyBase64 { get; set; } = "";
    public string IdentityKeyDigest { get; set; } = "";
    public string MaximumCapacityJson { get; set; } = "{}";
    public string BootstrapJson { get; set; } = "{}";
    public string Status { get; set; } = "Registered";
    public string? ReportedHeartbeatJson { get; set; }
    public long LastSequence { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
