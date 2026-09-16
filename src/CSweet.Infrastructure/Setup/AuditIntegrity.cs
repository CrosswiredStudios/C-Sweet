using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

public static class AuditIntegrity
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ComputeRecordHash(AuditEvent item)
    {
        var canonical = new
        {
            item.Id,
            item.OrganizationId,
            item.Category,
            item.Direction,
            item.Outcome,
            item.EventType,
            item.EntityType,
            item.EntityId,
            item.Summary,
            item.MetadataJson,
            item.OccurredAt,
            item.CreatedAt,
            item.TraceId,
            item.ParentEventId,
            item.ExternalMessageId,
            item.ExternalRequestId,
            item.CorrelationId,
            item.ActorKind,
            item.IdentityVerified,
            item.ActorApplicationUserId,
            item.ActorOrganizationUserId,
            item.ActorDisplayName,
            item.ActorAgentId,
            item.ActorInstallationId,
            item.ActorRuntimeInstanceId,
            item.ActorTickId,
            item.ActorSessionId,
            item.ActorPackageId,
            item.ActorPackageVersion,
            item.RemotePeer,
            item.TargetKind,
            item.TargetDisplayName,
            item.TargetAgentId,
            item.TargetInstallationId,
            item.TargetSessionId,
            item.ContentType,
            item.PayloadPreview,
            item.PayloadSha256,
            item.PayloadSize,
            item.PayloadTruncated,
            item.ErrorCode,
            item.ErrorMessage,
            item.PreviousRecordHash
        };
        var legacyHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions)));
        return item.IntegrityVersion < 2 ? legacyHash : Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { item.IntegrityVersion, legacyHash, item.EmployeesJson, item.EvidenceSha256 }, JsonOptions)));
    }
}
