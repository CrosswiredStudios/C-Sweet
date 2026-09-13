using CSweet.Compute.Contracts;

namespace CSweet.Compute.Runtime;

public sealed record ComputeObservationRecoveryPass(int Observed, int Deferred, Guid? NextAfterOperationId, int Acknowledged = 0);

/// <summary>Bounded expired-evidence recovery. Neither old evidence nor discovery grants physical authority.</summary>
public sealed class ComputeObservationRecovery(ComputeReplayJournal journal, ComputeDispatchVerifier verifier, TimeProvider clock)
{
    public async Task<ComputeObservationRecoveryPass> RecoverAsync(Guid? afterOperationId,
        Func<Guid, CancellationToken, Task<ComputeDispatchPacket?>> claim,
        Func<ComputeDispatchPacket, CancellationToken, Task> observe, CancellationToken token,
        Func<ComputeResultOutboxEntry, CancellationToken, Task<ComputeResultAcknowledgement?>>? readReceipt = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var rows = await journal.ListResultsAsync(afterOperationId, 100, linked.Token);
        var observed = 0; var deferred = 0; var acknowledged = 0;
        foreach (var row in rows)
        {
            linked.Token.ThrowIfCancellationRequested();
            var previous = row.Result;
            if (previous.ExpiresAt > clock.GetUtcNow()) continue;
            if (readReceipt is not null && await readReceipt(row, linked.Token) is { } receipt)
            {
                if (receipt.OperationId != previous.OperationId || receipt.Sequence != previous.Sequence || receipt.PayloadDigest != row.Digest || receipt.Applied || !Enum.IsDefined(receipt.Disposition))
                    throw new InvalidDataException("Recorded receipt does not match pending evidence.");
                if (await journal.AcknowledgeResultAsync(receipt, linked.Token)) acknowledged++;
                // A newer observation can race this historical receipt; its row remains pending.
                continue;
            }
            var packet = await claim(previous.OperationId, linked.Token);
            if (packet is null) { deferred++; continue; }
            var authority = verifier.Verify(packet).Authorization;
            if (authority.Mode != ComputeDispatchMode.Observe || authority.OperationId != previous.OperationId ||
                authority.EnvironmentId != previous.EnvironmentId || authority.OrganizationId != previous.OrganizationId ||
                authority.InstallationId != previous.InstallationId || authority.NodeId != previous.NodeId ||
                authority.ProviderId != previous.ProviderId || authority.Generation != previous.Generation ||
                authority.Action != previous.Action || authority.SpecificationDigest != previous.SpecificationDigest ||
                authority.ResourceId is not null && authority.ResourceId != previous.ResourceId)
                throw new UnauthorizedAccessException("Recovery requires observation-only authority for the queued operation.");
            // The executor re-verifies at use and observes under its journal fence. It must commit a
            // newly measured result; this layer never edits an old timestamp, sequence or physical claim.
            await observe(packet, linked.Token);
            observed++;
        }
        return new(observed, deferred, rows.Count == 100 ? rows[^1].Result.OperationId : null, acknowledged);
    }
}
