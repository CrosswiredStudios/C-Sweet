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
public sealed record RemoteMcpAccountView(string PackageDigest, IReadOnlyList<RemoteMcpConnectionView> Connections, IReadOnlyList<RemoteMcpConsumerView> Consumers);
