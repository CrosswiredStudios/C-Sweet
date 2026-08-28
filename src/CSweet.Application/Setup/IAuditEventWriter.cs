namespace CSweet.Application.Setup;

public interface IAuditEventWriter
{
    Task WriteAsync(
        string eventType,
        string entityType,
        Guid? entityId,
        string? summary,
        string? metadataJson = null,
        CancellationToken cancellationToken = default);

    Task<Guid> AppendAsync(
        AuditEventWriteRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This audit writer does not support structured ledger events.");
}

public sealed record AuditEventWriteRequest(
    string EventType,
    string Category = "Domain",
    string Direction = "Internal",
    string Outcome = "Completed",
    Guid? OrganizationId = null,
    string EntityType = "",
    Guid? EntityId = null,
    string? Summary = null,
    string? MetadataJson = null,
    DateTimeOffset? OccurredAt = null,
    Guid? TraceId = null,
    Guid? ParentEventId = null,
    string? ExternalMessageId = null,
    string? ExternalRequestId = null,
    string? CorrelationId = null,
    AuditActor? Actor = null,
    AuditTarget? Target = null,
    string? ContentType = null,
    ReadOnlyMemory<byte>? Payload = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool UseAmbientOrganization = true);

public sealed record AuditActor(
    string Kind,
    bool IdentityVerified = true,
    Guid? ApplicationUserId = null,
    Guid? OrganizationUserId = null,
    string? DisplayName = null,
    string? AgentId = null,
    Guid? InstallationId = null,
    Guid? RuntimeInstanceId = null,
    Guid? TickId = null,
    string? SessionId = null,
    string? PackageId = null,
    string? PackageVersion = null,
    string? RemotePeer = null);

public sealed record AuditTarget(
    string Kind,
    string? DisplayName = null,
    string? AgentId = null,
    Guid? InstallationId = null,
    string? SessionId = null);
