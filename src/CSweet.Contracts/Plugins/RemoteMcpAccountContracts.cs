namespace CSweet.Contracts.Plugins;
public sealed class RemoteMcpKeyRequest
{
    public string Key { get; set; } = "";
    public long? ExpectedGeneration { get; set; }
    public override string ToString() => "RemoteMcpKeyRequest [redacted]";
}
public sealed record RemoteMcpGrantRequest(Guid ConsumerInstallationId, long ExpectedGeneration, string PackageDigest);
public sealed record RemoteMcpRevokeRequest(long ExpectedGeneration);
public sealed record RemoteMcpConnectionView(Guid Id, long Generation, bool Revoked, Guid? ConsumerInstallationId);
public sealed record RemoteMcpConsumerView(Guid Id, string Name);
public sealed record RemoteMcpAccountView(string PackageDigest, IReadOnlyList<RemoteMcpConnectionView> Connections, IReadOnlyList<RemoteMcpConsumerView> Consumers)
{
    /// <summary>Provider display name from the reviewed manifest.</summary>
    public string ProviderName { get; init; } = "";
    /// <summary>The reviewed endpoint host that receives the key. Shown so the owner knows where the key is sent.</summary>
    public string EndpointHost { get; init; } = "";
    /// <summary>Reviewed read capabilities a grant can cover.</summary>
    public IReadOnlyList<RemoteMcpToolView> Tools { get; init; } = [];
}
public sealed record RemoteMcpToolView(string Capability, string Description);
