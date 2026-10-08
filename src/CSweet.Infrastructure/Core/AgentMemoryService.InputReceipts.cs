using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Domain.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    private sealed record ExtractionDispatchEvidence(int Version, Guid JobId, Guid LeaseToken, int RetryGeneration,
        MemoryPartition Partition, EnrichmentSources Sources);
    private sealed class MemoryInputReceiptCapacityException : Exception;

    private async Task RecordExtractionInputsAsync(EnrichmentInput input, Guid jobId, Guid leaseToken,
        Func<CancellationToken, Task> requireLease, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Extraction input evidence must commit before provider dispatch.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"MemoryCaptureOutbox\" WHERE \"Id\"={jobId} FOR UPDATE", token);
            await LockSourcesAsync(input.Episode, input.Sources, token);
        }
        await requireLease(token);
        await ValidateSourcesAsync(input.Episode, input.Sources, store, token);
        var generation = await db.MemoryCaptureOutbox.Where(x => x.Id == jobId).Select(x => x.RetryGeneration).SingleAsync(token);
        var json = JsonSerializer.Serialize(new ExtractionDispatchEvidence(1, jobId, leaseToken, generation, input.Episode.Partition, input.Sources));
        if (Encoding.UTF8.GetByteCount(json) > 16384) throw new MemorySourceInvalidatedException("memory_enrichment_unverifiable_output");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var existing = await db.MemoryExtractionInputReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == jobId && x.LeaseToken == leaseToken, token);
        if (existing is not null)
        {
            if (existing.EvidenceJson != json || existing.ReceiptHash != digest)
                throw new MemorySourceInvalidatedException("memory_enrichment_unverifiable_output");
        }
        else
        {
            if (await db.MemoryExtractionInputReceipts.CountAsync(x => x.JobId == jobId, token) >= 64)
                throw new MemoryInputReceiptCapacityException();
            var receipt = new MemoryExtractionInputReceipt { Id = Guid.NewGuid(), OrganizationId = Guid.Parse(input.Episode.Partition.TenantId),
                JobId = jobId, LeaseToken = leaseToken, RetryGeneration = generation, EvidenceJson = json,
                ReceiptHash = digest, CreatedAt = DateTimeOffset.UtcNow };
            db.MemoryExtractionInputReceipts.Add(receipt);
            try { await db.SaveChangesAsync(token); }
            catch { db.Entry(receipt).State = EntityState.Detached; throw; }
        }
        await requireLease(token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    internal static ExtractionErasureEvidence InspectDispatchInputsForErasure(MemoryExtractionInputReceipt receipt)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(receipt.EvidenceJson) > 16384 || receipt.ReceiptHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receipt.EvidenceJson))))
                throw new JsonException();
            using (var document = JsonDocument.Parse(receipt.EvidenceJson)) CheckFields(document.RootElement);
            var value = JsonSerializer.Deserialize<ExtractionDispatchEvidence>(receipt.EvidenceJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, RespectRequiredConstructorParameters = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new JsonException();
            if (value.Version != 1 || value.JobId != receipt.JobId || value.LeaseToken != receipt.LeaseToken ||
                value.RetryGeneration != receipt.RetryGeneration || value.JobId == Guid.Empty || value.LeaseToken == Guid.Empty || value.RetryGeneration < 0 ||
                value.Partition is null || value.Partition.TenantId != receipt.OrganizationId.ToString("D") || value.Sources is not { } sources ||
                sources.ConversationId == Guid.Empty || sources.InstallationId == Guid.Empty || sources.Messages is not { Length: >= 1 and <= 2 } ||
                sources.Messages[0].Role != ConversationRole.User || sources.Messages.Any(x => x.Id == Guid.Empty || x.Checksum is not { Length: 64 } || !x.Checksum.All(Uri.IsHexDigit)) ||
                sources.Messages.Select(x => x.Id).Distinct().Count() != sources.Messages.Length ||
                sources.Messages.Length == 2 && sources.Messages[1].Role != ConversationRole.Assistant) throw new JsonException();
            return new(value.Partition, sources.ConversationId, sources.InstallationId,
                sources.Messages.Select(x => new ExtractionErasureInput(x.Id, x.Role, x.Checksum, x.CreatedAt)).ToArray());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new InvalidOperationException("memory_erasure_capture_lineage_review_required", error); }
    }
}
