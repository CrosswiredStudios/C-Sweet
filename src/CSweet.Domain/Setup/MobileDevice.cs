namespace CSweet.Domain.Setup;

/// <summary>
/// A phone that asked to use this Headquarters for one person (H4). Identified by its device key pin, which is both
/// its relay admission key and its TLS client key. Becomes usable only after the person approves it from a trusted
/// session; changing the person's security stamp (password change, sign out everywhere) requires approval again.
/// </summary>
public sealed class MobileDevice
{
    public Guid Id { get; set; }
    public Guid ApplicationUserId { get; set; }
    /// <summary>base64url SHA-256 of the phone key's SubjectPublicKeyInfo. Unique.</summary>
    public string KeyPin { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public MobileDeviceState State { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>The person's security stamp when the phone was approved.</summary>
    public string? SecurityStampAtApproval { get; set; }
    public long Revision { get; set; } = 1;
}

public enum MobileDeviceState
{
    Pending = 0,
    Active = 1,
    Denied = 2,
    Revoked = 3
}
