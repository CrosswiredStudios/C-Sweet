namespace CSweet.Domain.Setup;

/// <summary>Content-free lifecycle record retained independently of provider inventory and credentials.</summary>
public sealed class PluginAccountDeletion
{
    public Guid AccountGroupId { get; set; }
    public Guid PluginInstallationId { get; set; }
    public string Status { get; set; } = "Queued";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset TargetAt { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public DateTimeOffset? LocalCompletedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;
}

/// <summary>A host-recorded copy or external deletion obligation. Agents cannot acknowledge erasure.</summary>
public sealed class PluginAccountDataCopy
{
    public Guid Id { get; set; }
    public Guid AccountGroupId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid ConsumerInstallationId { get; set; }
    public string Store { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? CleanupNextAttemptAt {get;set;}
    public long Revision { get; set; } = 1;
}

public sealed class PluginAccountAdministration
{
    public Guid PluginInstallationId { get; set; }
    public DateTimeOffset? RemovalRequestedAt { get; set; }
    public long Revision { get; set; } = 1;
}

/// <summary>Content-free signed confirmation retained after the source account is erased.</summary>
public sealed class PluginAccountCleanupReceipt
{
    public Guid CopyId {get;set;}
    public long CopyRevision {get;set;}
    public string RequestHash {get;set;}="";
    public string KeyId {get;set;}="";
    public string Payload {get;set;}="";
    public string Signature {get;set;}="";
    public DateTimeOffset ConfirmedAt {get;set;}
}
