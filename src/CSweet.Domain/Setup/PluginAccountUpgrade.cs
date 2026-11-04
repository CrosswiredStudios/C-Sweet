namespace CSweet.Domain.Setup;

/// <summary>Durable, content-free intent/receipt correlation. Never an OAuth or deletion receipt.</summary>
public sealed class PluginAccountUpgrade
{
    public Guid Id {get;set;}
    public Guid SourceInstallationId {get;set;}
    public Guid TargetInstallationId {get;set;}
    public Guid AccountGroupId {get;set;}
    public Guid ConnectionId {get;set;}
    public Guid OriginalSetupSessionId {get;set;}
    public Guid ApplicationUserId {get;set;}
    public string SourceSnapshotHash {get;set;}="";
    public string ServiceOrigin {get;set;}="";
    public Guid RemoteConnectionId {get;set;}
    public string ServiceRequestJson {get;set;}="";
    public string ServiceReviewHash {get;set;}="";
    public string CompatibilityHash {get;set;}="";
    public string Status {get;set;}="Pending";
    public long Revision {get;set;}=1;
    public DateTimeOffset CreatedAt {get;set;}
    public DateTimeOffset? CompletedAt {get;set;}
}
