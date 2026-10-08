using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Memory;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    private sealed record ProcedureEvidence(ProceduralMemory Procedure, IReadOnlyDictionary<Guid, MemoryEpisode> Sources,
        Guid[] SourceIds, bool Valid, MemorySensitivity Sensitivity, long Revision, string Token);

    public async Task<MemoryProcedureReviewResponse> GetProcedureAsync(Guid organizationId, Guid employeeId, Guid procedureId,
        Guid applicationUserId, CancellationToken cancellationToken = default)
    {
        await RequireBackendAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
        await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
        await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
        var procedure = await LockProcedureAsync(store, procedureId, cancellationToken);
        await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, procedure.Partition, cancellationToken);
        var evidence = await ReadProcedureEvidenceAsync(store, procedure, cancellationToken);
        var sharedHash = await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor, evidence.Sources.Values, cancellationToken);
        evidence = evidence with { Token = SourceOperatorToken(evidence.Token, sharedHash) };
        await transaction.CommitAsync(cancellationToken);
        var current = procedure.ValidFrom <= clock.GetUtcNow() && (procedure.ValidTo is null || procedure.ValidTo > clock.GetUtcNow());
        return new(procedure.Id, evidence.Revision, evidence.Token, procedure.Name, procedure.Procedure, procedure.Applicability, procedure.Version,
            procedure.Confirmation.ToString(), evidence.Sensitivity.ToString(), evidence.Valid,
            sharedHash is null && evidence.Valid && procedure.Version < int.MaxValue && evidence.SourceIds.Length <= MemoryProvenance.MaximumSourceEpisodes,
            current, evidence.SourceIds);
    }

    public async Task<ReviewMemoryProcedureResponse> ReviewProcedureAsync(Guid organizationId, Guid employeeId, Guid procedureId,
        Guid applicationUserId, ReviewMemoryProcedureRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRevision <= 0 || request.EvidenceToken?.Length != 64 ||
            request.Action is not ("confirm" or "reject" or "correct") ||
            (request.Action == "correct" ? request.Correction is not { } correction || string.IsNullOrWhiteSpace(correction.Name) ||
                correction.Name.Length > 200 || string.IsNullOrWhiteSpace(correction.Procedure) || correction.Procedure.Length > 16000 ||
                correction.Applicability?.Length > 4000 : request.Correction is not null)) throw new ArgumentException("Invalid procedure review.");
        await RequireBackendAsync(cancellationToken);
        await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, false, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var actor = await MemoryManagerAuthorization.RequireAsync(db, organizationId, employeeId, applicationUserId, true, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString("D") + ":" + request.OperationId.ToString("D")}, 0))", cancellationToken);
            await MemoryReviewWriteBarrier.AcquireAsync(db, cancellationToken);
            await using var store = new PostgreSqlMemoryStore((NpgsqlTransaction)transaction.GetDbTransaction());
            var procedure = await LockProcedureAsync(store, procedureId, cancellationToken);
            await MemoryManagerAuthorization.RequirePartitionAsync(db, organizationId, employeeId, actor, procedure.Partition, cancellationToken);
            var evidence = await ReadProcedureEvidenceAsync(store, procedure, cancellationToken);
            var sharedHash = await RequireSharedReviewSourcesAsync(organizationId, employeeId, applicationUserId, actor, evidence.Sources.Values, cancellationToken);
            evidence = evidence with { Token = SourceOperatorToken(evidence.Token, sharedHash) };
            var hash = Hash(new { organizationId, employeeId, procedureId, applicationUserId, actor, request });
            var receipt = await db.MemoryReviewReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.OperationId == request.OperationId, cancellationToken);
            if (receipt is not null)
            {
                if (receipt.RecordKind != "Procedure" || receipt.RequestHash != hash) throw Changed();
                return ProcedureResponse(receipt, true);
            }

            if (evidence.Revision != request.ExpectedRevision || evidence.Token != request.EvidenceToken) throw Changed();
            var now = clock.GetUtcNow();
            if (procedure.ValidFrom > now || procedure.ValidTo <= now || !Enum.IsDefined(procedure.Confirmation) ||
                (request.Action != "reject" && !evidence.Valid)) throw new InvalidOperationException("memory_review_source_unavailable");
            if ((request.Action == "confirm" && procedure.Confirmation == MemoryConfirmationState.Confirmed) ||
                (request.Action == "reject" && procedure.Confirmation == MemoryConfirmationState.Rejected)) throw Changed();
            var resultId = procedure.Id;
            if (request.Action == "correct")
            {
                if (sharedHash is not null) throw new InvalidOperationException("memory_shared_correction_requires_restricted_source_lineage");
                MemoryProvenance.ValidateSourceEpisodes(evidence.SourceIds);
                if (procedure.Version == int.MaxValue) throw new InvalidOperationException("memory_procedure_version_limit");
                var replacement = request.Correction!;
                var sourceId = Guid.NewGuid(); resultId = Guid.NewGuid();
                var expiry = evidence.Sources.Values.Select(x => x.ExpiresAt).Append(procedure.ValidTo).Where(x => x.HasValue).Min();
                var content = replacement.Name + "\n" + replacement.Procedure + "\nApplicability: " + replacement.Applicability;
                await store.AppendEpisodeAsync(new(sourceId, procedure.Partition, InferScope(procedure.Partition), content, "text/plain",
                    new("user", request.OperationId.ToString("D"), actor.ToString("D")), Hash(content), now, now,
                    ExpiresAt: expiry, LegalHold: evidence.Sources.Values.Any(x => x.LegalHold), Sensitivity: evidence.Sensitivity,
                    OperationalReferences: [new("memory-procedure", procedure.Id.ToString("D"), evidence.Revision.ToString())]), cancellationToken);
                await store.WriteProcedureAsync(procedure with { Id = resultId, EpisodeId = sourceId, Name = replacement.Name,
                    Procedure = replacement.Procedure, Applicability = replacement.Applicability, Version = procedure.Version + 1,
                    Trust = MemoryTrustTier.ConfirmedUser, Confirmation = MemoryConfirmationState.Confirmed, ValidFrom = now,
                    RecordedAt = now, SourceEpisodeIds = evidence.SourceIds }, cancellationToken);
                await UpdateProcedureAsync(procedure with { ValidTo = now }, cancellationToken);
            }
            else await UpdateProcedureAsync(procedure with { Confirmation = request.Action == "confirm"
                ? MemoryConfirmationState.Confirmed : MemoryConfirmationState.Rejected }, cancellationToken);
            receipt = new MemoryReviewReceipt
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, EmployeeId = employeeId, RecordKind = "Procedure", MemoryId = procedureId,
                OperationId = request.OperationId, ActorApplicationUserId = applicationUserId, ActorOrganizationUserId = actor,
                RequestHash = hash, Action = request.Action, PreviousRevision = evidence.Revision, ResultMemoryId = resultId,
                ResultRevision = await RevisionAsync(procedure.Partition, resultId, cancellationToken, MemoryRecordKind.Procedure), CreatedAt = now
            };
            db.MemoryReviewReceipts.Add(receipt);
            db.QueueAudit(new AuditEventWriteRequest("memory.procedure.reviewed.v1", "Memory", OrganizationId: organizationId,
                EntityType: "MemoryProcedure", EntityId: procedureId, Summary: "A human reviewer changed a memory procedure.",
                MetadataJson: JsonSerializer.Serialize(new { receipt.Action, receipt.PreviousRevision, receipt.ResultRevision, receipt.ResultMemoryId }),
                OccurredAt: now, CorrelationId: request.OperationId.ToString("D"),
                Actor: new AuditActor("Human", ApplicationUserId: applicationUserId, OrganizationUserId: actor),
                EventId: receipt.Id, Employees: [new(employeeId, "Affected")], UseAmbientOrganization: false));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ProcedureResponse(receipt, false);
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    private async Task<ProceduralMemory> LockProcedureAsync(PostgreSqlMemoryStore store, Guid id, CancellationToken token)
    {
        await using var command = Command("""
            SELECT partition_key,payload::text FROM csweet_memory_procedures p WHERE id=@id AND partition_key LIKE 'mp2:%'
                AND NOT EXISTS(SELECT 1 FROM csweet_memory_partition_migration_rows q WHERE q.table_name='csweet_memory_procedures'
                    AND q.record_id=p.id::text AND q.disposition='Quarantine') FOR UPDATE
            """);
        command.Parameters.AddWithValue("id", id);
        ProceduralMemory procedure;
        string partitionKey;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException();
            partitionKey = reader.GetString(0);
            procedure = JsonSerializer.Deserialize<ProceduralMemory>(reader.GetString(1), JsonOptions) ?? throw new KeyNotFoundException();
        }
        if (procedure.Id != id || procedure.Partition.StorageKey != partitionKey ||
            !(await store.ReadRevisionsAsync(procedure.Partition, MemoryRecordKind.Procedure, id, limit: 1, cancellationToken: token)).Items.Any())
            throw new InvalidOperationException("memory_review_source_unavailable");
        return procedure;
    }

    private async Task<ProcedureEvidence> ReadProcedureEvidenceAsync(PostgreSqlMemoryStore store, ProceduralMemory procedure, CancellationToken token)
    {
        if (!MemoryProvenance.HasBoundedSources(procedure.SourceEpisodeIds)) throw new InvalidOperationException("memory_review_source_unavailable");
        var ids = procedure.SourceEpisodeIds.Append(procedure.EpisodeId).Distinct().Order().ToArray();
        var sources = new Dictionary<Guid, MemoryEpisode>();
        foreach (var id in ids)
            if (await store.GetEpisodeAsync(procedure.Partition, id, token) is { } source) sources[id] = source;
        var now = clock.GetUtcNow();
        var valid = sources.Count == ids.Length && sources.All(x => MemoryProvenance.IsCurrent(x.Value, procedure.Partition, x.Key, now)) &&
            Enum.IsDefined(procedure.Trust) && Enum.IsDefined(procedure.Confirmation) && procedure.Version > 0 &&
            procedure.ValidFrom <= now && (procedure.ValidTo is null || procedure.ValidTo > now);
        var sensitivity = sources.Count == ids.Length ? MemoryProvenance.Maximum(sources.Values.Select(x => x.Sensitivity).ToArray()) : MemorySensitivity.Restricted;
        var revision = await RevisionAsync(procedure.Partition, procedure.Id, token, MemoryRecordKind.Procedure);
        return new(procedure, sources, ids, valid, sensitivity, revision,
            Hash(new { procedure, sources = sources.Values.OrderBy(x => x.Id).ToArray(), ids, revision, valid }));
    }

    private async Task UpdateProcedureAsync(ProceduralMemory procedure, CancellationToken token)
    {
        await using var command = Command("""
            UPDATE csweet_memory_procedures SET confirmation=@confirmation,valid_to=@validTo,payload=@payload
            WHERE id=@id AND partition_key=@partition
            """);
        command.Parameters.AddWithValue("id", procedure.Id); command.Parameters.AddWithValue("partition", procedure.Partition.StorageKey);
        command.Parameters.AddWithValue("confirmation", (int)procedure.Confirmation);
        command.Parameters.AddWithValue("validTo", NpgsqlDbType.TimestampTz, (object?)procedure.ValidTo ?? DBNull.Value);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(procedure, JsonOptions));
        if (await command.ExecuteNonQueryAsync(token) != 1) throw Changed();
    }

    private static ReviewMemoryProcedureResponse ProcedureResponse(MemoryReviewReceipt receipt, bool replay) =>
        new(receipt.Id, receipt.MemoryId, receipt.ResultMemoryId, receipt.ResultRevision, receipt.Action, receipt.CreatedAt, replay);
}
