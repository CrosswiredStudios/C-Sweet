namespace CSweet.Domain.Setup;

/// <summary>
/// This Headquarters is linked to C-Sweet (hosted onboarding). No row means Private. At most one active row.
/// The administrator's C-Sweet identity is stored as an ASP.NET Identity external login (issuer + subject).
/// </summary>
public sealed class InstallationLink
{
    public Guid Id { get; set; }
    /// <summary>Account issuer this installation trusts, for example https://account.c-sweet.com/.</summary>
    public string Issuer { get; set; } = string.Empty;
    /// <summary>Installation id assigned by the issuer at registration.</summary>
    public Guid InstallationId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>ECDSA P-256 private key (PKCS#8), protected with ASP.NET Data Protection. Never log or return it.</summary>
    public string ProtectedInstallationKey { get; set; } = string.Empty;
    public Guid LinkedByApplicationUserId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
    public InstallationLinkState State { get; set; }
    public long Revision { get; set; } = 1;
}

public enum InstallationLinkState
{
    Active = 0,
    Retired = 1
}
