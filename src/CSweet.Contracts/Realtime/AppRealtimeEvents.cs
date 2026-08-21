using System.Text.Json;

namespace CSweet.Contracts.Realtime;

public static class AppRealtimeEvents
{
    public const string NotificationCreated = "com.csweet.app.notification.created.v1";
    public const string NotificationUpdated = "com.csweet.app.notification.updated.v1";
    public const string WorkBoardChanged = "com.csweet.app.work-board.changed.v1";
    public const string AgentHireOperationChanged = "com.csweet.app.agent-hire-operation.changed.v1";
    public const string BusinessOnboardingOperationChanged = "com.csweet.app.business-onboarding-operation.changed.v1";
    public const string EmployeeDirectoryChanged = "com.csweet.app.employee-directory.changed.v1";
}

public sealed record AgentHireOperationChangedEvent(Guid OperationId, Guid OrganizationId, string Status);

public sealed record BusinessOnboardingOperationChangedEvent(Guid OperationId, Guid OrganizationId, string Status);

public sealed record EmployeeDirectoryChangedEvent(
    Guid OrganizationId,
    Guid OrganizationUserId,
    string ChangeKind);

public sealed record AppRealtimeEventEnvelope(
    Guid EventId,
    long Sequence,
    string EventType,
    Guid? OrganizationId,
    string Subject,
    DateTimeOffset OccurredAt,
    JsonElement Data);

public sealed record AppNotificationEvent(
    Guid NotificationId,
    Guid OrganizationId,
    Guid RecipientOrganizationUserId,
    Guid? OriginatingAgentOrganizationUserId,
    string Severity,
    string Category,
    string Title,
    string Body,
    string? ActionUri,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? DismissedAt);
