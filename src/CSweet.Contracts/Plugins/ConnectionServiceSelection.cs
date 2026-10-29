namespace CSweet.Contracts.Plugins;

public sealed record ConnectionServiceChoice(string Mode, string? Origin, long Revision);
public sealed record SaveConnectionServiceChoice(string Mode, string? Origin, long ExpectedRevision);
public sealed record ConnectionServiceProbe(string State, bool OAuthAccountSetup, bool MobilePairing, bool GeneralProviderActions);
public sealed record OwnerBootstrapDetails(string Action, string ServiceOrigin, Guid BindingId, string PublicKey, string KeyId,
    Guid ActorId, string TenantScope, string[] Capabilities, long ExpectedRevision);
public sealed record OwnerAccessSummary(Guid BindingId, Guid ActorId, long GrantRevision, string TenantScope, string[] Capabilities);
