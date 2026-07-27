using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Data;

namespace CSweet.Infrastructure.Setup;

public sealed class AuditEventWriter : IAuditEventWriter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditExecutionContextAccessor _executionContext;
    private readonly IDataProtector _protector;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _streamLocks = new(StringComparer.Ordinal);

    public AuditEventWriter(
        IServiceScopeFactory scopeFactory,
        IAuditExecutionContextAccessor executionContext,
        IDataProtectionProvider dataProtectionProvider)
    {
        _scopeFactory = scopeFactory;
        _executionContext = executionContext;
        _protector = dataProtectionProvider.CreateProtector("CSweet.SecurityAuditLedger.v1");
    }

    public async Task WriteAsync(
        string eventType,
        string entityType,
        Guid? entityId,
        string? summary,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        await AppendAsync(new AuditEventWriteRequest(eventType, EntityType: entityType,
            EntityId: entityId, Summary: summary, MetadataJson: metadataJson), cancellationToken);
    }

    public async Task<Guid> AppendAsync(AuditEventWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EventType);
        var ambient = _executionContext.Current;
        var organizationId = request.OrganizationId ?? ambient?.OrganizationId;
        var actor = request.Actor ?? ambient?.Actor ?? new AuditActor("Platform", DisplayName: "C-Sweet platform");
        var streamKey = organizationId?.ToString("D") ?? "system";
        var streamLock = _streamLocks.GetOrAdd(streamKey, static _ => new SemaphoreSlim(1, 1));
        await streamLock.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            IDbContextTransaction? transaction = null;
            if (db.Database.IsRelational())
            {
                // PostgreSQL's transaction-scoped advisory lock serializes writers for a
                // single audit stream. Using Serializable as well can abort otherwise
                // independent business transactions with SQLSTATE 40001 while they emit
                // audit records through this separate DbContext.
                var isolationLevel = db.Database.IsNpgsql()
                    ? IsolationLevel.ReadCommitted
                    : IsolationLevel.Serializable;
                transaction = await db.Database.BeginTransactionAsync(isolationLevel, cancellationToken);
            }
            await using (transaction)
            {
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtext({streamKey}))", cancellationToken);

                var previousHash = await db.AuditEvents.AsNoTracking()
                    .Where(x => x.OrganizationId == organizationId && x.IntegrityVersion == 1)
                    .OrderByDescending(x => x.Sequence)
                    .Select(x => x.RecordHash)
                    .FirstOrDefaultAsync(cancellationToken);
                // PostgreSQL stores timestamptz values with microsecond precision. Normalize
                // before hashing so the material read back from the database is byte-for-byte
                // equivalent to the material that was sealed.
                var now = NormalizeTimestamp(DateTimeOffset.UtcNow);
                var payload = request.Payload is { } body
                    ? AuditPayloadSanitizer.Capture(body, request.ContentType)
                    : null;
                var item = new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    Category = Clean(request.Category, 48, "Domain"),
                    Direction = Clean(request.Direction, 32, "Internal"),
                    Outcome = Clean(request.Outcome, 48, "Completed"),
                    EventType = Clean(request.EventType, 160, "unknown"),
                    EntityType = Clean(request.EntityType, 160, string.Empty),
                    EntityId = request.EntityId,
                    Summary = CleanOptional(request.Summary, 1024),
                    MetadataJson = AuditPayloadSanitizer.RedactJson(request.MetadataJson),
                    OccurredAt = request.OccurredAt.HasValue
                        ? NormalizeTimestamp(request.OccurredAt.Value)
                        : now,
                    CreatedAt = now,
                    TraceId = request.TraceId ?? ambient?.TraceId ?? Guid.NewGuid(),
                    ParentEventId = request.ParentEventId ?? ambient?.ParentEventId,
                    ExternalMessageId = CleanOptional(request.ExternalMessageId, 200),
                    ExternalRequestId = CleanOptional(request.ExternalRequestId, 200),
                    CorrelationId = CleanOptional(request.CorrelationId, 200),
                    ActorKind = Clean(actor.Kind, 32, "Unknown"),
                    IdentityVerified = actor.IdentityVerified,
                    ActorApplicationUserId = actor.ApplicationUserId,
                    ActorOrganizationUserId = actor.OrganizationUserId,
                    ActorDisplayName = CleanOptional(actor.DisplayName, 256),
                    ActorAgentId = CleanOptional(actor.AgentId, 256),
                    ActorInstallationId = actor.InstallationId,
                    ActorRuntimeInstanceId = actor.RuntimeInstanceId,
                    ActorTickId = actor.TickId,
                    ActorSessionId = CleanOptional(actor.SessionId, 128),
                    ActorPackageId = CleanOptional(actor.PackageId, 256),
                    ActorPackageVersion = CleanOptional(actor.PackageVersion, 80),
                    RemotePeer = CleanOptional(actor.RemotePeer, 256),
                    TargetKind = CleanOptional(request.Target?.Kind, 32),
                    TargetDisplayName = CleanOptional(request.Target?.DisplayName, 256),
                    TargetAgentId = CleanOptional(request.Target?.AgentId, 256),
                    TargetInstallationId = request.Target?.InstallationId,
                    TargetSessionId = CleanOptional(request.Target?.SessionId, 128),
                    ContentType = CleanOptional(request.ContentType, 160),
                    PayloadPreview = payload?.Preview,
                    PayloadSha256 = payload?.Sha256,
                    PayloadSize = payload?.Size,
                    PayloadTruncated = payload?.Truncated ?? false,
                    ErrorCode = CleanOptional(request.ErrorCode, 160),
                    ErrorMessage = CleanOptional(request.ErrorMessage, 2048),
                    IntegrityVersion = 1,
                    PreviousRecordHash = previousHash
                };
                item.RecordHash = AuditIntegrity.ComputeRecordHash(item);
                item.IntegritySeal = _protector.Protect(item.RecordHash);
                db.AuditEvents.Add(item);
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                return item.Id;
            }
        }
        finally
        {
            streamLock.Release();
        }
    }

    private static string Clean(string? value, int maximum, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private static string? CleanOptional(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    public static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
