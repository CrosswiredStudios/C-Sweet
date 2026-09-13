namespace CSweet.Compute.Contracts;

public static class InfrastructureActions
{
    public const string Provision = "compute.provision.v1";
    public const string Read = "compute.read.v1";
    public const string List = "compute.list.v1";
    public const string Start = "compute.start.v1";
    public const string Stop = "compute.stop.v1";
    public const string Restart = "compute.restart.v1";
    public const string Destroy = "compute.destroy.v1";
    public const string Persist = "compute.persist.v1";
    public const string Execute = "compute.execute.v1";
    public const string Snapshot = "compute.snapshot.v1";
    public const string Resize = "compute.resize.v1";
    public const string StorageCreate = "storage.create.v1";
    public const string StorageAttach = "storage.attach.v1";
    public const string StorageDetach = "storage.detach.v1";
    public const string StoragePersist = "storage.persist.v1";
    public const string StorageDelete = "storage.delete.v1";
    public const string Outbound = "network.outbound.v1";
    public const string Inbound = "network.inbound.v1";
    public const string PublicEndpoint = "network.public-endpoint.v1";
    public const string PublishPort = "network.publish-port.v1";
    public const string PrivateNetwork = "network.create-private-network.v1";
    public const string DnsCreate = "dns.create-record.v1";
    public const string DnsUpdate = "dns.update-record.v1";
    public const string DnsDelete = "dns.delete-record.v1";
}

public sealed record ComputeActionAuthorization(Guid GrantId, long Revision, string Action, DateTimeOffset ExpiresAt);
