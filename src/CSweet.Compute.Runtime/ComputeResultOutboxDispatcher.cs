using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public sealed class ComputeResultExpiredException : Exception;

public sealed record ComputeResultDeliveryPass(int Acknowledged, IReadOnlyList<ComputeResultOutboxEntry> RequiresObservation, Guid? NextAfterOperationId);

/// <summary>One bounded recovery page. Delivery never holds the physical-operation journal lock.</summary>
public sealed class ComputeResultOutboxDispatcher(ComputeReplayJournal journal, TimeProvider clock)
{
    public async Task<ComputeResultDeliveryPass> DispatchAsync(Guid? afterOperationId,
        Func<ComputeResultOutboxEntry, CancellationToken, Task<ComputeResultAcknowledgement>> deliver,
        CancellationToken token)
    {
        var rows = await journal.ListResultsAsync(afterOperationId, 100, token);
        var expired = new List<ComputeResultOutboxEntry>(); var acknowledged = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Result.ExpiresAt <= clock.GetUtcNow())
            {
                // Keep the original evidence. A caller must obtain fresh observation authority;
                // an outbox entry is neither a dispatch grant nor authority to renew timestamps.
                expired.Add(row); continue;
            }
            ComputeResultAcknowledgement acknowledgement;
            try { acknowledgement = await deliver(row, token); }
            catch (ComputeResultExpiredException) when (row.Result.ExpiresAt <= clock.GetUtcNow())
            { expired.Add(row); continue; }
            if (acknowledgement.OperationId != row.Result.OperationId || acknowledgement.Sequence != row.Result.Sequence ||
                acknowledgement.PayloadDigest != row.Digest)
                throw new InvalidDataException("Delivery acknowledged different provider evidence.");
            if (await journal.AcknowledgeResultAsync(acknowledgement, token)) acknowledged++;
        }
        return new(acknowledged, expired, rows.Count == 100 ? rows[^1].Result.OperationId : null);
    }
}
