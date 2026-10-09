using System.Text;
using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryService
{
    internal sealed record EpisodeErasureEvidence(MemoryEpisode Episode, long Revision, MemoryErasureTarget[] References);

    internal static bool IsVerifiedProposalForOperatorReview(MemoryEpisode episode,Guid organization,Guid employee,Guid installation)
    {
        var json=JsonSerializer.Serialize(new EpisodeJobSource(episode,1));
        try
        {
            InspectEpisodeForErasure(new() { Id=episode.Id,OrganizationId=organization,EmployeeId=employee,InstallationId=installation,
                EpisodeId=episode.Id,SourceJson=json,SourceHash=SourceChecksum(json) });
            return episode.Source.Type=="agent-proposal";
        }
        catch(InvalidOperationException) { return false; }
    }

    // Inspect immutable retained evidence without imposing recall eligibility or requiring
    // an old producer grant to remain enabled. The coordinator supplies current human authority.
    internal static EpisodeErasureEvidence InspectEpisodeForErasure(MemoryEpisodeEnrichmentJob job, bool includeAccepted = true)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(job.SourceJson) > 1_048_576 || job.SourceHash != SourceChecksum(job.SourceJson)) throw new JsonException();
            using (var document = JsonDocument.Parse(job.SourceJson)) CheckFields(document.RootElement);
            var source = JsonSerializer.Deserialize<EpisodeJobSource>(job.SourceJson) ?? throw new JsonException();
            var episode = source.Episode;
            if (source.Revision <= 0 || episode?.Partition is null || episode.Source is null || episode.Id != job.EpisodeId ||
                !MemorySourceIntegrity.IsVerified(episode) || !Enum.IsDefined(episode.Sensitivity) ||
                episode.Partition.TenantId != job.OrganizationId.ToString("D") ||
                episode.Partition.ApplicationId != "csweet" && episode.Partition.ApplicationId != job.InstallationId.ToString("D") ||
                source.Reconciliation is not null && !ValidReconciliation(source.Reconciliation) ||
                job.InputGeneration == 0 && (job.PreviousJobId is not null || source.Reextraction is not null) ||
                job.InputGeneration != 0 && (!ValidReextraction(source.Reextraction) || source.Reextraction!.Generation != job.InputGeneration ||
                    source.Reextraction.PreviousJobId != job.PreviousJobId)) throw new JsonException();
            if (episode.Source.Type == "agent-proposal")
            {
                if (episode.Source.Id != episode.Id.ToString("D") || episode.Source.Author != job.EmployeeId.ToString("D") ||
                    ReadGuid(episode.Metadata,"installationId") != job.InstallationId || job.ReviewerApplicationUserId is not null ||
                    episode.TransferEvidence is not null || episode.OperationalReferences is not null || episode.Sensitivity < MemorySensitivity.Personal)
                    throw new JsonException();
            }
            else if (episode.Source.Type == WorkInstructionMemorySource.Type)
            {
                if (ReadGuid(episode.Metadata, "installationId") != job.InstallationId ||
                    ReadGuid(episode.Metadata, "publicationId") is null || ReadGuid(episode.Metadata, "workItemId") is null ||
                    job.ReviewerApplicationUserId is null || episode.TransferEvidence is not null || episode.CorrectionEvidence is not null ||
                    episode.Sensitivity != MemorySensitivity.Internal) throw new JsonException();
            }
            else if (episode.Source.Type != "knowledge-transfer" || episode.TransferEvidence is null || job.ReviewerApplicationUserId is null)
                throw new JsonException();
            if (includeAccepted && job.AcceptedExtractionJson is { } json)
            {
                if (Encoding.UTF8.GetByteCount(json)>1_048_576) throw new JsonException();
                using (var document=JsonDocument.Parse(json)) CheckFields(document.RootElement);
                var accepted=JsonSerializer.Deserialize<AcceptedMemoryExtraction>(json) ?? throw new JsonException();
                if (!HasVerifiableEnvelope(accepted) || accepted.Enrichment is null || string.IsNullOrWhiteSpace(accepted.ExtractorVersion) ||
                    accepted.Provider?.Id==Guid.Empty || string.IsNullOrWhiteSpace(accepted.Provider?.Model) ||
                    accepted.Provider?.ConfigurationHash is not { Length:64 } || !MatchesEpisodeExtraction(accepted,source) || accepted.GenericSourceHash != job.SourceHash ||
                    JsonSerializer.Serialize(accepted.Episode)!=JsonSerializer.Serialize(episode) || job.ExtractionAcceptedAt is null) throw new JsonException();
            }
            else if (includeAccepted && (job.Status==MemoryCaptureStatus.Completed || job.ExtractionAcceptedAt is not null)) throw new JsonException();
            var references=new HashSet<MemoryErasureTarget> { new(MemoryErasureKind.Episode,episode.Id,episode.Partition) };
            foreach(var id in source.Reconciliation?.SourceEpisodeIds ?? []) references.Add(new(MemoryErasureKind.Episode,id,episode.Partition));
            foreach(var record in episode.TransferEvidence?.Records ?? [])
            {
                if (record is null || record.Partition is null || record.Id==Guid.Empty || record.Revision<=0 ||
                    record.Kind is < MemoryRecordKind.Episode or > MemoryRecordKind.Procedure) throw new JsonException();
                references.Add(new((MemoryErasureKind)(int)record.Kind,record.Id,record.Partition));
            }
            if (references.Count>641) throw new JsonException();
            return new(episode,source.Revision,references.OrderBy(x=>x.Partition.StorageKey,StringComparer.Ordinal).ThenBy(x=>x.Kind).ThenBy(x=>x.Id).ToArray());
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
        { throw new InvalidOperationException("memory_erasure_generic_lineage_review_required",error); }
    }
}
