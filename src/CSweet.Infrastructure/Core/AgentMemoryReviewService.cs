using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService(CSweetDbContext db, IMemoryStore memory, TimeProvider clock,
    IDataProtectionProvider? protection = null) : IAgentMemoryReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record Evidence(MemoryClaim Claim, MemoryEntity Subject, MemoryEntity? Object,
        IReadOnlyDictionary<Guid, MemoryEpisode> Sources, Guid[] SourceIds, bool Valid, MemorySensitivity Sensitivity,
        long Revision, string Token);

    public async Task<MemoryClaimReviewResponse> GetClaimAsync(Guid organizationId, Guid employeeId, Guid claimId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var claim = await LockClaimAsync(store, claimId, cancellationToken);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, claim.Partition, cancellationToken);
        var evidence = await ReadEvidenceAsync(store, claim, cancellationToken);
        var sharedHash = await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor, evidence.Sources.Values, cancellationToken);
        evidence = evidence with { Token = SourceOperatorToken(evidence.Token, sharedHash) };
        await transaction.CommitAsync(cancellationToken);
        return new(claim.Id, evidence.Revision, evidence.Token,
            $"{evidence.Subject.CanonicalName} {claim.Predicate} {claim.Value ?? evidence.Object?.CanonicalName}",
            claim.Confirmation.ToString(), evidence.Sensitivity.ToString(), evidence.Valid,
            sharedHash is null && evidence.Valid && evidence.SourceIds.Length <= MemoryProvenance.MaximumSourceEpisodes, claim.ValidFrom <= clock.GetUtcNow() &&
                (claim.ValidTo is null || claim.ValidTo > clock.GetUtcNow()), evidence.SourceIds, claim.ObjectEntityId is not null);
    }

    public async Task<ReviewMemoryClaimResponse> ReviewClaimAsync(Guid organizationId, Guid employeeId, Guid claimId,
        Guid applicationUserId, ReviewMemoryClaimRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64 ||
            request.Action is not ("confirm" or "reject" or "correct") ||
            (request.Action == "correct" ? request.ReplacementEntity is { } target
                ? target.EntityId == Guid.Empty || target.EvidenceToken?.Length != 64 || request.ReplacementValue is not null
                : string.IsNullOrWhiteSpace(request.ReplacementValue) || request.ReplacementValue.Length > 16000
                : request.ReplacementValue is not null || request.ReplacementEntity is not null)) throw new ArgumentException("Invalid memory review.");
        await RequireBackendAsync(cancellationToken);
        // Check authority before starting the write path; check again while holding current authority rows.
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
            // Serialize identical operation identities even when submitted against different claims.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")}, 0))", cancellationToken);
            await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var claim = await LockClaimAsync(store, claimId, cancellationToken);
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, claim.Partition, cancellationToken);
            var evidence = await ReadEvidenceAsync(store, claim, cancellationToken);
            var sharedHash = await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor, evidence.Sources.Values, cancellationToken);
            evidence = evidence with { Token = SourceOperatorToken(evidence.Token, sharedHash) };
            var hash = Hash(new { organizationId, employeeId, claimId, applicationUserId, actor, request });
            var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.RecordKind != "Claim" || receipt.RequestHash != hash) throw Changed();
                return Response(receipt, true);
            }

            if (evidence.Revision != request.ExpectedRevision || evidence.Token != request.EvidenceToken) throw Changed();
            var now = clock.GetUtcNow();
            if (claim.ValidFrom > now || claim.ValidTo <= now || !Enum.IsDefined(claim.Confirmation) ||
                (request.Action != "reject" && !evidence.Valid)) throw new InvalidOperationException("memory_review_source_unavailable");
            if ((request.Action == "confirm" && claim.Confirmation == MemoryConfirmationState.Confirmed) ||
                (request.Action == "reject" && claim.Confirmation == MemoryConfirmationState.Rejected)) throw Changed();
            var resultId = claim.Id;
            if (request.Action == "correct")
            {
                if (sharedHash is not null) throw new InvalidOperationException("memory_shared_correction_requires_restricted_source_lineage");
                if ((claim.ObjectEntityId is not null) != (request.ReplacementEntity is not null))
                    throw new ArgumentException("A correction must preserve the claim's value type.");
                var replacement = request.ReplacementEntity is { } selected
                    ? await ReadCorrectionTargetAsync(store, claim, selected.EntityId, cancellationToken) : null;
                if (replacement is not null && await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor,
                    replacement.Sources.Values, cancellationToken) is not null)
                    throw new InvalidOperationException("memory_shared_correction_requires_restricted_source_lineage");
                if (request.ReplacementEntity is { } expected && (replacement is null || replacement.Token != expected.EvidenceToken)) throw Changed();
                // Retain original evidence, so correction cannot silently detach sensitivity or lifecycle restrictions.
                var contributors = evidence.SourceIds.Concat(replacement?.Entity.SourceEpisodeIds ?? []).Distinct().Order().ToArray();
                MemoryProvenance.ValidateSourceEpisodes(contributors);
                var sources = evidence.Sources.Values.Concat(replacement?.Sources.Values ?? []).DistinctBy(x => x.Id).ToArray();
                var sensitivity = MemoryProvenance.Maximum(evidence.Sensitivity, replacement?.Sensitivity ?? evidence.Sensitivity);
                var episodeId = Guid.NewGuid(); resultId = Guid.NewGuid();
                var expiry = sources.Select(x => x.ExpiresAt).Append(claim.ValidTo).Where(x => x.HasValue).Min();
                var content = replacement is null ? request.ReplacementValue! : $"{evidence.Subject.CanonicalName} {claim.Predicate} {replacement.Entity.CanonicalName}";
                var episode = new MemoryEpisode(episodeId, claim.Partition, InferScope(claim.Partition), content,
                    "text/plain", new("user", request.OperationId.ToString("D"), actor.ToString("D")),
                    Hash(content), now, now, ExpiresAt: expiry,
                    LegalHold: sources.Any(x => x.LegalHold), Sensitivity: sensitivity,
                    OperationalReferences: replacement is null ? [new("memory-claim", claim.Id.ToString("D"), evidence.Revision.ToString())]
                        : [new("memory-claim", claim.Id.ToString("D"), evidence.Revision.ToString()),
                           new("memory-entity", replacement.Entity.Id.ToString("D"), replacement.Revision.ToString())]);
                await store.AppendEpisodeAsync(episode, cancellationToken);
                await store.WriteClaimAsync(claim with { Id = resultId, EpisodeId = episodeId, Value = request.ReplacementValue,
                    Confirmation = MemoryConfirmationState.Confirmed, Trust = MemoryTrustTier.ConfirmedUser,
                    ObjectEntityId = replacement?.Entity.Id, Sensitivity = sensitivity, ValidFrom = now, RecordedAt = now,
                    SupersedesClaimId = claim.Id, SourceEpisodeIds = contributors }, cancellationToken);
                await store.SupersedeClaimAsync(claim.Id, resultId, now, cancellationToken);
            }
            else await store.SetClaimConfirmationAsync(claim.Id, request.Action == "confirm"
                ? MemoryConfirmationState.Confirmed : MemoryConfirmationState.Rejected, cancellationToken);
            receipt = new MemoryReviewReceipt
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId, RecordKind = "Claim", MemoryId = claimId,
                OperationId = request.OperationId, ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor,
                RequestHash = hash, Action = request.Action, PreviousRevision = evidence.Revision, ResultMemoryId = resultId,
                ResultRevision = await RevisionAsync(claim.Partition, resultId, cancellationToken), CreatedAt = now
            };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.claim.reviewed.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryClaim", EntityId: claimId, Summary: "A human reviewer changed a memory claim.",
                MetadataJson: JsonSerializer.Serialize(new { receipt.Action, receipt.PreviousRevision, receipt.ResultRevision, ResultClaimId = receipt.ResultMemoryId }),
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Response(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task RequireBackendAsync(CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || memory is not PostgreSqlMemoryStore)
            throw new NotSupportedException("Memory review requires the shared PostgreSQL transaction boundary.");
        await memory.InitializeAsync(token);
    }

    private async Task<MemoryClaim> LockClaimAsync(PostgreSqlMemoryStore store, Guid id, CancellationToken token)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM csweet_memory_claims WHERE id={id} FOR UPDATE", token);
        var claim = await store.GetClaimAsync(id, token) ?? throw new KeyNotFoundException();
        if (claim.Id != id || !(await store.ReadRevisionsAsync(claim.Partition, MemoryRecordKind.Claim, id, limit: 1, cancellationToken: token)).Items.Any())
            throw new InvalidOperationException("memory_review_source_unavailable");
        return claim;
    }

    private async Task<Evidence> ReadEvidenceAsync(PostgreSqlMemoryStore store, MemoryClaim claim, CancellationToken token)
    {
        if (!MemoryProvenance.HasBoundedSources(claim.SourceEpisodeIds)) throw new InvalidOperationException("memory_review_source_unavailable");
        var entities = new List<MemoryEntity>();
        foreach (var id in new[] { (Guid?)claim.SubjectEntityId, claim.ObjectEntityId }.OfType<Guid>().Distinct().Order())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM csweet_memory_entities WHERE id={id} AND partition_key={claim.Partition.StorageKey} FOR SHARE", token);
            await using var command = Command("SELECT payload::text FROM csweet_memory_entities WHERE id=@id AND partition_key=@partition");
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("partition", claim.Partition.StorageKey);
            var entity = JsonSerializer.Deserialize<MemoryEntity>((string?)await command.ExecuteScalarAsync(token) ?? "null", JsonOptions);
            if (entity is null || entity.Id != id || entity.Partition != claim.Partition || !MemoryProvenance.HasBoundedSources(entity.SourceEpisodeIds))
                throw new InvalidOperationException("memory_review_source_unavailable");
            entities.Add(entity);
        }
        var ids = new[] { claim.EpisodeId }.Concat(claim.SourceEpisodeIds).Concat(entities.SelectMany(x => x.SourceEpisodeIds)).Distinct().Order().ToArray();
        var sources = new Dictionary<Guid, MemoryEpisode>();
        await using (var command = Command("SELECT id,payload::text FROM csweet_memory_episodes WHERE partition_key=@partition AND id=ANY(@ids) ORDER BY id FOR SHARE"))
        {
            command.Parameters.AddWithValue("partition", claim.Partition.StorageKey); command.Parameters.AddWithValue("ids", ids);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var source = JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(1), JsonOptions);
                if (source is not null && source.Id == reader.GetGuid(0) && source.Partition == claim.Partition) sources[source.Id] = source;
            }
        }
        foreach (var source in sources.Values.Where(x => x.TransferEvidence is not null || x.CorrectionEvidence is not null || x.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true || x.Source.Type == "knowledge-transfer").ToArray())
            sources[source.Id] = await store.GetEpisodeAsync(claim.Partition, source.Id, token) ?? source;
        var now = clock.GetUtcNow();
        var valid = sources.Count == ids.Length && sources.All(x => MemoryProvenance.IsCurrent(x.Value, claim.Partition, x.Key, now));
        var sensitivity = MemoryProvenance.Maximum(entities.Select(x => x.Sensitivity).Concat(sources.Values.Select(x => x.Sensitivity)).Append(claim.Sensitivity).ToArray());
        valid &= Enum.IsDefined(claim.Sensitivity) && entities.All(x => Enum.IsDefined(x.Sensitivity)) &&
            claim.ValidFrom <= now && (claim.ValidTo is null || claim.ValidTo > now);
        var revision = await RevisionAsync(claim.Partition, claim.Id, token);
        return new(claim, entities.Single(x => x.Id == claim.SubjectEntityId), entities.FirstOrDefault(x => x.Id == claim.ObjectEntityId),
            sources, ids, valid, sensitivity, revision, Hash(new { claim, entities, sources = sources.Values.OrderBy(x => x.Id).ToArray(), ids, revision, valid }));
    }

    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
        (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());

    private async Task<long> RevisionAsync(MemoryPartition partition, Guid id, CancellationToken token, MemoryRecordKind kind = MemoryRecordKind.Claim)
    {
        await using var command = Command("SELECT coalesce(max(revision),0) FROM csweet_memory_revisions WHERE partition_key=@partition AND kind=@kind AND record_id=@id");
        command.Parameters.AddWithValue("partition", partition.StorageKey); command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("kind", (int)kind);
        var revision = (long)(await command.ExecuteScalarAsync(token))!;
        if (revision <= 0) throw new InvalidOperationException("memory_review_revision_unavailable");
        return revision;
    }

    private static MemoryScope InferScope(MemoryPartition partition) => partition.CustomNamespace == "organization" ? MemoryScope.Tenant :
        partition.UserId is not null ? MemoryScope.User :
        partition.AgentId is not null ? MemoryScope.Agent : MemoryScope.Application;
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))).ToLowerInvariant();
    private static DbUpdateConcurrencyException Changed() => new("memory_review_changed");
    private static ReviewMemoryClaimResponse Response(MemoryReviewReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.MemoryId, receipt.ResultMemoryId, receipt.ResultRevision, receipt.Action, receipt.CreatedAt, replay);
}
