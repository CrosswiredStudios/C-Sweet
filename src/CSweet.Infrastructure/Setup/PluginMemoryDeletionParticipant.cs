using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Provider-neutral confirmation of native memory erasure, not a second erasure authority.</summary>
public sealed class PluginMemoryDeletionParticipant(CSweetDbContext db) : IPluginAccountDataDeletionParticipant
{
    public string Store => "external:memory";

    public async Task<bool> DeleteAsync(Guid accountId, Guid? organizationId, Guid consumerInstallationId, Guid recordId, CancellationToken ct)
    {
        if (organizationId is not { } org || recordId != consumerInstallationId ||
            !await db.AgentInstallations.AnyAsync(i => i.Id == consumerInstallationId && i.BusinessId == org.ToString("D"), ct) ||
            !await db.PluginAccountGroups.AnyAsync(g => g.Id == accountId && g.State == PluginAccountState.Disconnecting, ct)) return false;
        var cutoff = await db.PluginAccountDeletions.Where(d => d.AccountGroupId == accountId && d.CompletedAt == null)
            .Select(d => (DateTimeOffset?)d.RequestedAt).SingleOrDefaultAsync(ct);
        if (cutoff is null) return false;
        var copies = await db.PluginAccountDataCopies.AsNoTracking().Where(c => c.AccountGroupId == accountId &&
            c.OrganizationId == org && c.ConsumerInstallationId == consumerInstallationId).Take(4097).ToArrayAsync(ct);
        if (copies.Length > 4096 || copies.Any(c => c.CreatedAt > cutoff) ||
            !copies.Any(c => c.Store == Store && c.RecordId == recordId) ||
            !copies.Any(c=>c.Store is nameof(ConversationMessage) or nameof(ChatTurn) or "memory-origin")) return false;

        var messages = copies.Where(c => c.Store == nameof(ConversationMessage)).ToArray();
        foreach (var source in messages)
        {
            var captures = await db.MemoryCaptureOutbox.AsNoTracking().Where(j => j.ConversationMessageId == source.RecordId).Take(129).ToArrayAsync(ct);
            if (captures.Length > 128) return false;
            // No capture means no native conversational memory sink. An existing queue row,
            // including an unfinished one, needs the reviewed erasure fence and receipt.
            if (captures.Length == 0)
            {
                // Deleting a source/queue row is not proof that its saved context was erased.
                if(!await db.CoreConversationMessages.AnyAsync(m=>m.Id==source.RecordId,ct) &&
                    !await AgentMemoryReviewService.HasConfirmedErasureAsync(db,org,source.RecordId,source.CreatedAt,ct)) return false;
                continue;
            }
            if (captures.Any(j => j.LastError != MemoryCaptureErasure.FailureCode || j.Status != MemoryCaptureStatus.Failed ||
                j.AcceptedExtractionJson != null || j.ExtractionAcceptedAt != null || j.LeaseToken != null || j.LeaseExpiresAt != null) ||
                !await db.MemorySourceInvalidations.AnyAsync(i => i.SourceMessageId == source.RecordId && i.ReasonCode == MemoryCaptureErasure.FailureCode, ct) ||
                !await AgentMemoryReviewService.HasConfirmedErasureAsync(db, org, source.RecordId, source.CreatedAt, ct)) return false;
        }
        foreach (var source in copies.Where(c => c.Store == "memory-origin"))
        {
            if (!await AgentMemoryReviewService.HasConfirmedErasureAsync(db, org, source.RecordId, source.CreatedAt, ct) ||
                await db.MemoryEpisodeEnrichmentJobs.AnyAsync(j => j.OrganizationId == org && j.EpisodeId == source.RecordId, ct)) return false;
            var captures=await db.MemoryCaptureOutbox.AsNoTracking().Where(j=>j.ConversationMessageId==source.RecordId).Take(129).ToArrayAsync(ct);
            if(captures.Length>128 || captures.Any(j=>j.LastError!=MemoryCaptureErasure.FailureCode || j.Status!=MemoryCaptureStatus.Failed ||
                j.AcceptedExtractionJson!=null || j.ExtractionAcceptedAt!=null || j.LeaseToken!=null || j.LeaseExpiresAt!=null)) return false;
            if(captures.Length>0 && !await db.MemorySourceInvalidations.AnyAsync(i=>i.SourceMessageId==source.RecordId && i.ReasonCode==MemoryCaptureErasure.FailureCode,ct)) return false;
        }
        var turnIds = copies.Where(c => c.Store == nameof(ChatTurn)).Select(c => c.RecordId).ToArray();
        var turns = await db.ChatTurns.AsNoTracking().Where(t => turnIds.Contains(t.Id)).ToArrayAsync(ct);
        // Missing protected evidence is not an acknowledgement. Native tombstones survive.
        if (turns.Length != turnIds.Length || turns.Any(t => t.MemoryErasedAt == null)) return false;
        var jobIds = copies.Where(c => c.Store == nameof(MemoryEpisodeEnrichmentJob)).Select(c => c.RecordId).ToArray();
        return !await db.MemoryEpisodeEnrichmentJobs.AnyAsync(j => jobIds.Contains(j.Id), ct);
    }
}
