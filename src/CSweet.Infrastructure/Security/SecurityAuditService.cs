using CSweet.Application.Security;
using CSweet.Contracts.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

public sealed class SecurityAuditService(
    CSweetDbContext db,
    IDataProtectionProvider dataProtectionProvider) : ISecurityAuditService
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("CSweet.SecurityAuditLedger.v1");

    public async Task<SecurityEventPageResponse> BrowseAsync(
        Guid organizationId,
        SecurityEventQuery query,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var events = db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (DecodeCursor(query.Cursor) is { } before) events = events.Where(x => x.Sequence < before);
        if (query.From.HasValue) events = events.Where(x => x.OccurredAt >= query.From.Value);
        if (query.To.HasValue) events = events.Where(x => x.OccurredAt <= query.To.Value);
        if (!string.IsNullOrWhiteSpace(query.Category)) events = events.Where(x => x.Category == query.Category);
        if (!string.IsNullOrWhiteSpace(query.Direction)) events = events.Where(x => x.Direction == query.Direction);
        if (!string.IsNullOrWhiteSpace(query.Outcome)) events = events.Where(x => x.Outcome == query.Outcome);
        if (!string.IsNullOrWhiteSpace(query.ActorKind)) events = events.Where(x => x.ActorKind == query.ActorKind);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            events = events.Where(x => x.EventType.Contains(search) ||
                (x.Summary != null && x.Summary.Contains(search)) ||
                (x.ActorDisplayName != null && x.ActorDisplayName.Contains(search)) ||
                (x.ActorAgentId != null && x.ActorAgentId.Contains(search)) ||
                (x.TargetDisplayName != null && x.TargetDisplayName.Contains(search)) ||
                (x.TargetAgentId != null && x.TargetAgentId.Contains(search)) ||
                x.EntityType.Contains(search) ||
                (x.ExternalMessageId != null && x.ExternalMessageId.Contains(search)) ||
                (x.ExternalRequestId != null && x.ExternalRequestId.Contains(search)) ||
                (x.CorrelationId != null && x.CorrelationId.Contains(search)));
        }

        var page = await events.OrderByDescending(x => x.Sequence).Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var requiredPreviousHashes = page.Select(x => x.PreviousRecordHash).Where(x => x != null).Cast<string>().Distinct().ToList();
        var existingHashes = requiredPreviousHashes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            (await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.RecordHash != null && requiredPreviousHashes.Contains(x.RecordHash))
                .Select(x => x.RecordHash!).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var items = page.Select(x => new SecurityEventSummaryResponse(
            x.Id, x.Sequence, x.OccurredAt, x.Category, x.Direction, x.Outcome, x.EventType,
            x.ActorKind, ActorLabel(x), x.TargetDisplayName ?? x.TargetAgentId, x.Summary,
            x.CorrelationId, IntegrityStatus(x, x.PreviousRecordHash is null || existingHashes.Contains(x.PreviousRecordHash)),
            x.EntityType, x.EntityId)).ToList();
        return new SecurityEventPageResponse(items,
            hasMore && page.Count > 0 ? EncodeCursor(page[^1].Sequence) : null);
    }

    public async Task<SecurityEventDetailResponse?> GetAsync(
        Guid organizationId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var x = await db.AuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == eventId && x.OrganizationId == organizationId, cancellationToken);
        if (x is null) return null;
        var chainExists = x.PreviousRecordHash is null || await db.AuditEvents.AsNoTracking().AnyAsync(previous =>
            previous.OrganizationId == organizationId && previous.RecordHash == x.PreviousRecordHash, cancellationToken);
        var children = await db.AuditEvents.AsNoTracking().Where(child => child.OrganizationId == organizationId &&
            child.ParentEventId == x.Id).OrderBy(child => child.Sequence).Select(child => child.Id).ToListAsync(cancellationToken);
        return new SecurityEventDetailResponse
        {
            Id = x.Id, Sequence = x.Sequence, OrganizationId = organizationId,
            OccurredAt = x.OccurredAt, RecordedAt = x.CreatedAt, Category = x.Category,
            Direction = x.Direction, Outcome = x.Outcome, EventType = x.EventType,
            TraceId = x.TraceId, ParentEventId = x.ParentEventId, ChildEventIds = children,
            ExternalMessageId = x.ExternalMessageId, ExternalRequestId = x.ExternalRequestId,
            CorrelationId = x.CorrelationId, ActorKind = x.ActorKind,
            IdentityVerified = x.IdentityVerified, ActorApplicationUserId = x.ActorApplicationUserId,
            ActorOrganizationUserId = x.ActorOrganizationUserId, ActorDisplayName = x.ActorDisplayName,
            ActorAgentId = x.ActorAgentId, ActorInstallationId = x.ActorInstallationId,
            ActorRuntimeInstanceId = x.ActorRuntimeInstanceId, ActorTickId = x.ActorTickId,
            ActorSessionId = x.ActorSessionId, ActorPackageId = x.ActorPackageId,
            ActorPackageVersion = x.ActorPackageVersion, RemotePeer = x.RemotePeer,
            TargetKind = x.TargetKind, TargetDisplayName = x.TargetDisplayName,
            TargetAgentId = x.TargetAgentId, TargetInstallationId = x.TargetInstallationId,
            TargetSessionId = x.TargetSessionId, EntityType = x.EntityType, EntityId = x.EntityId,
            Summary = x.Summary, MetadataJson = x.MetadataJson, ContentType = x.ContentType,
            PayloadPreview = x.PayloadPreview, PayloadSha256 = x.PayloadSha256,
            PayloadSize = x.PayloadSize, PayloadTruncated = x.PayloadTruncated,
            ErrorCode = x.ErrorCode, ErrorMessage = x.ErrorMessage,
            PreviousRecordHash = x.PreviousRecordHash, RecordHash = x.RecordHash,
            IntegrityStatus = IntegrityStatus(x, chainExists)
        };
    }

    private string IntegrityStatus(AuditEvent item, bool chainExists)
    {
        if (item.IntegrityVersion == 0) return "LegacyUnsealed";
        if (item.IntegrityVersion != 1 || string.IsNullOrWhiteSpace(item.RecordHash) ||
            string.IsNullOrWhiteSpace(item.IntegritySeal)) return "Invalid";
        try
        {
            return chainExists && string.Equals(AuditIntegrity.ComputeRecordHash(item), item.RecordHash, StringComparison.Ordinal) &&
                string.Equals(_protector.Unprotect(item.IntegritySeal), item.RecordHash, StringComparison.Ordinal)
                ? "Verified" : "Invalid";
        }
        catch
        {
            return "Invalid";
        }
    }

    private static string ActorLabel(AuditEvent item) =>
        item.ActorDisplayName ?? item.ActorAgentId ?? item.ActorApplicationUserId?.ToString("D") ?? item.ActorKind;

    private static string EncodeCursor(long sequence) =>
        Convert.ToBase64String(BitConverter.GetBytes(sequence)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static long? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == sizeof(long) ? BitConverter.ToInt64(bytes) : null;
        }
        catch (FormatException) { return null; }
    }
}
