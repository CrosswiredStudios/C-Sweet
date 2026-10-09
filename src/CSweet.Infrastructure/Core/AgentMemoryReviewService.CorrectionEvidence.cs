using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Memory;

namespace CSweet.Infrastructure.Core;

public sealed partial class AgentMemoryReviewService
{
    // The immutable review receipt is the ownership authority for a human correction.
    // Shared organization partitions deliberately contain no employee ID.
    private async Task<(MemoryReviewReceipt Receipt, string Payload)> ReadCorrectionReviewAsync(
        Guid organization, MemoryEpisode episode, CancellationToken token)
    {
        if (episode.CorrectionEvidence is not { } correction || episode.TransferEvidence is not null ||
            !MemorySourceIntegrity.IsVerified(episode) || correction.ReviewOperationId == Guid.Empty ||
            correction.Sources is not { Count: > 0 and <= MemoryProvenance.MaximumSourceEpisodes } ||
            episode.Source.Type != "user" || episode.Source.Id != correction.ReviewOperationId.ToString("D") ||
            !Guid.TryParseExact(episode.Source.Author, "D", out var reviewer))
            throw new UnresolvedTransferRetention();

        await using var command = Command("""
            SELECT ((to_jsonb(r) - 'ClaimId' - 'ResultClaimId') ||
                jsonb_build_object('MemoryId',r."ClaimId",'ResultMemoryId',r."ResultClaimId"))::text
            FROM "MemoryReviewReceipts" r
            WHERE r."OrganizationId"=@organization AND r."OperationId"=@operation FOR SHARE
            """);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("operation", correction.ReviewOperationId);
        var payload = await command.ExecuteScalarAsync(token) as string ?? throw new UnresolvedTransferRetention();
        var receipt = JsonSerializer.Deserialize<MemoryReviewReceipt>(payload, JsonOptions)
            ?? throw new UnresolvedTransferRetention();
        var referenceType = receipt.RecordKind switch
        {
            "Claim" => "memory-claim", "Procedure" => "memory-procedure", "Block" => "memory-block", _ => null
        };
        if (receipt.Action != "correct" || receipt.ActorOrganizationUserId != reviewer ||
            receipt.OperationId != correction.ReviewOperationId || receipt.OrganizationId != organization ||
            receipt.EmployeeId == Guid.Empty || referenceType is null || episode.OperationalReferences is null ||
            !episode.OperationalReferences.Any(x => x.Type == referenceType && x.Id == receipt.MemoryId.ToString("D") &&
                x.Version == receipt.PreviousRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            throw new UnresolvedTransferRetention();
        return (receipt, payload);
    }

    private async Task<Guid> ReadCorrectionErasureOwnerAsync(Guid organization, MemoryEpisode episode,
        CancellationToken token)
    {
        try
        {
            var (receipt, _) = await ReadCorrectionReviewAsync(organization, episode, token);
            if (episode.Partition.AgentId is { } employee && employee != receipt.EmployeeId.ToString("D"))
                throw new UnresolvedTransferRetention();
            return receipt.EmployeeId;
        }
        catch (Exception error) when (error is UnresolvedTransferRetention or JsonException or
            ArgumentException or NullReferenceException)
        {
            // Authorization, database failures and lock contention still propagate.
            throw new InvalidOperationException("memory_erasure_source_review_required", error);
        }
    }
}
