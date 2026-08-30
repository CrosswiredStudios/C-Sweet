using CSweet.Domain.Core;

namespace CSweet.Domain.Communications;

public static class CommunicationProviderKeys
{
    public const string Discord = "discord";
    public const string InApp = "in-app";
}
public enum CommunicationConnectionStatus { Pending, Connected, Degraded, Paused, Disconnected }
public enum CommunicationWorkspaceMode { Dedicated, Contained }
public enum ManagedResourceKind { Category, Channel, Role, Webhook, Command, Thread }
public enum CommunicationDeliveryStatus { Pending, Leased, Delivered, Failed, DeadLettered, Cancelled }
public enum CommunicationDeliveryKind
{
    ProvisionEmployee,
    UpdateEmployee,
    ArchiveEmployee,
    SendMessage,
    ReconcileWorkspace,
    DisconnectWorkspace,
    Notification,
    RegisterLinkCode,
    AssignIdentity
}
public enum NotificationSeverity { Routine, Important, Urgent }

public sealed class CommunicationConnection
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public Guid? PluginInstallationId { get; set; }
    public string WorkspaceExternalId { get; set; } = string.Empty;
    public string? ManagedRootExternalId { get; set; }
    public CommunicationWorkspaceMode WorkspaceMode { get; set; }
    public CommunicationConnectionStatus Status { get; set; }
    public string ConfigurationJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Durable result of accepting a provider message into the platform.</summary>
public sealed class CommunicationIngressReceipt
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public Guid OrganizationId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? ErrorCode { get; set; }
    public string ResultMessage { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A provider identity linked once at the C-Sweet installation boundary.</summary>
public sealed class ExternalIdentity
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string ExternalUserId { get; set; } = string.Empty;
    public Guid ApplicationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ManagedExternalResource
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? OrganizationUserId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public ManagedResourceKind Kind { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? ParentExternalId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ExternalIdentityLink
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public string ExternalUserId { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public Guid? ActiveDirectAgentOrganizationUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class ExternalIdentityLinkCode
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}

public sealed class ExternalMessageReference
{
    public Guid Id { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public string ChannelExternalId { get; set; } = string.Empty;
    public string MessageExternalId { get; set; } = string.Empty;
    public string? ThreadExternalId { get; set; }
    public bool IsInbound { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CommunicationDelivery
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid? ConnectionId { get; set; }
    public Guid? OrganizationUserId { get; set; }
    public Guid? ConversationMessageId { get; set; }
    public CommunicationDeliveryKind Kind { get; set; }
    public CommunicationDeliveryStatus Status { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? ExternalReceiptId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class UserNotification
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RecipientOrganizationUserId { get; set; }
    public Guid? OriginatingAgentOrganizationUserId { get; set; }
    public NotificationSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? ActionUri { get; set; }
    public string? DeduplicationKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}

public sealed class NotificationPreference
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? QuietHoursStart { get; set; }
    public string? QuietHoursEnd { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public NotificationSeverity MinimumSeverity { get; set; }
}
