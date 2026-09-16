using CSweet.Application.Security;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using CSweet.Contracts.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Security;

public sealed partial class SecurityAuditService(
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
        if (query.EmployeeId is Guid employee)
            events = events.Where(x => db.AuditEventEmployees.Any(a => a.AuditEventId == x.Id && a.OrganizationId == organizationId && a.EmployeeId == employee) || x.ActorOrganizationUserId == employee);
        // Collapse the full employee-scoped set BEFORE cursor/date/outcome filtering.
        // Correlation IDs identify a turn and can span several separate model calls.
        var scopedEvents = events;
        if (query.GroupModelResponses)
            events = events.Where(x => x.EntityType != "AgentRunLog" || x.EntityId == null ||
                (x.EventType != "model.response.chunk" && x.EventType != "model.call.completed") ||
                !scopedEvents.Any(other => other.EntityType == "AgentRunLog" && other.EntityId == x.EntityId &&
                    (other.EventType == "model.response.chunk" || other.EventType == "model.call.completed") &&
                    (other.OccurredAt > x.OccurredAt || (other.OccurredAt == x.OccurredAt && other.Sequence > x.Sequence))));
        if (query.OrderByOccurrence && query.Cursor is not null)
        {
            var before = DecodeOccurrenceCursor(query.Cursor);
            events = events.Where(x => x.OccurredAt < before.Time || (x.OccurredAt == before.Time && x.Sequence < before.Sequence));
        }
        else if (DecodeCursor(query.Cursor) is { } before) events = events.Where(x => x.Sequence < before);
        if (!string.IsNullOrWhiteSpace(query.CorrelationId)) events = events.Where(x => x.CorrelationId == query.CorrelationId);
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

        var ordered = query.OrderByOccurrence ? events.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Sequence) : events.OrderByDescending(x => x.Sequence);
        var page = await ordered.Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var requiredPreviousHashes = page.Select(x => x.PreviousRecordHash).Where(x => x != null).Cast<string>().Distinct().ToList();
        var existingHashes = requiredPreviousHashes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) :
            (await db.AuditEvents.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                x.RecordHash != null && requiredPreviousHashes.Contains(x.RecordHash))
                .Select(x => x.RecordHash!).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var pageIds = page.Select(x => x.Id).ToArray();
        var roles = query.EmployeeId is Guid employeeFilter
            ? await db.AuditEventEmployees.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.EmployeeId == employeeFilter && pageIds.Contains(x.AuditEventId)).ToListAsync(cancellationToken)
            : [];
        var responseIds = query.GroupModelResponses ? page.Where(IsModelResponse).Select(x => x.EntityId!.Value).ToArray() : [];
        var chunkCounts = responseIds.Length == 0 ? new Dictionary<Guid, int>() :
            await scopedEvents.Where(x => x.EntityType == "AgentRunLog" && x.EntityId.HasValue && responseIds.Contains(x.EntityId.Value) && x.EventType == "model.response.chunk")
                .GroupBy(x => x.EntityId!.Value).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var items = page.Select(x => new SecurityEventSummaryResponse(
            x.Id, x.Sequence, x.OccurredAt, x.Category, x.Direction, x.Outcome, query.GroupModelResponses && IsModelResponse(x) ? "model.response" : x.EventType,
            x.ActorKind, ActorLabel(x), x.TargetDisplayName ?? x.TargetAgentId, x.Summary,
            x.CorrelationId, IntegrityStatus(x, x.PreviousRecordHash is null || existingHashes.Contains(x.PreviousRecordHash)),
            x.EntityType, x.EntityId, x.ActorOrganizationUserId == query.EmployeeId && query.EmployeeId.HasValue ? "Actor" :
                roles.Where(a => a.AuditEventId == x.Id).OrderBy(a => a.Role == "Actor" ? 0 : a.Role == "Recipient" ? 1 : 2).FirstOrDefault()?.Role,
            query.GroupModelResponses && IsModelResponse(x) ? chunkCounts.GetValueOrDefault(x.EntityId!.Value) : null)).ToList();
        return new SecurityEventPageResponse(items,
            hasMore && page.Count > 0 ? (query.OrderByOccurrence ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new OccurrenceCursor(page[^1].OccurredAt, page[^1].Sequence))) : EncodeCursor(page[^1].Sequence)) : null);
    }

    public async Task<SecurityEventDetailResponse?> GetAsync(
        Guid organizationId,
        Guid eventId,
        CancellationToken cancellationToken = default,
        bool groupModelResponse = false,
        Guid? employeeId = null)
    {
        var x = await db.AuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == eventId && x.OrganizationId == organizationId, cancellationToken);
        if (x is null) return null;
        var chainExists = x.PreviousRecordHash is null || await db.AuditEvents.AsNoTracking().AnyAsync(previous =>
            previous.OrganizationId == organizationId && previous.RecordHash == x.PreviousRecordHash, cancellationToken);
        var children = await db.AuditEvents.AsNoTracking().Where(child => child.OrganizationId == organizationId &&
            child.ParentEventId == x.Id).OrderBy(child => child.Sequence).Select(child => child.Id).ToListAsync(cancellationToken);
        var payload = await db.AuditEventPayloads.AsNoTracking().SingleOrDefaultAsync(p => p.AuditEventId == eventId, cancellationToken);
        string? content = null;
        var availability = payload is null ? (x.PayloadPreview is null ? "NotRecorded" : "PreviewOnly") : "Available";
        var integrity = IntegrityStatus(x, chainExists);
        if (payload is not null)
        {
            try
            {
                var bytes = dataProtectionProvider.CreateProtector("CSweet.AuditPayload.v1").Unprotect(payload.ProtectedContent);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != x.EvidenceSha256)
                { availability = "IntegrityFailed"; integrity = "Invalid"; }
                else content = Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException) { availability = "ProtectionUnavailable"; integrity = "Invalid"; }
        }
        return new SecurityEventDetailResponse
        {
            ModelResponse = groupModelResponse && IsModelResponse(x) ? await AssembleModelResponseAsync(organizationId, x.EntityId!.Value, employeeId, cancellationToken) : null,
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
            IntegrityStatus = integrity, PayloadContent = content, PayloadAvailability = availability,
            EmployeesJson = x.EmployeesJson
        };
    }

    private string IntegrityStatus(AuditEvent item, bool chainExists)
    {
        if (item.IntegrityVersion == 0) return "LegacyUnsealed";
        if (item.IntegrityVersion is not (1 or 2) || string.IsNullOrWhiteSpace(item.RecordHash) ||
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

    private sealed record OccurrenceCursor(DateTimeOffset Time, long Sequence);
    private static OccurrenceCursor DecodeOccurrenceCursor(string cursor)
    {
        try { return JsonSerializer.Deserialize<OccurrenceCursor>(Convert.FromBase64String(cursor)) ?? throw new FormatException(); }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new ArgumentException("Invalid audit cursor.", nameof(cursor)); }
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
