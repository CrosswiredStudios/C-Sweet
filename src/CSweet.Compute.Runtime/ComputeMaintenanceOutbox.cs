namespace CSweet.Compute.Runtime;

/// <summary>Immutable queued evidence. Transport must authenticate it and retain EventId for receiver deduplication.</summary>
public sealed record ComputeMaintenanceOutboxEntry(Guid Id, DateTimeOffset OccurredAt, string EventJson, string Digest);

/// <summary>Network delivery never holds the physical journal lock. Lost acknowledgements leave evidence pending.</summary>
public sealed class ComputeMaintenanceOutboxDispatcher(ComputeReplayJournal journal)
{
    public async Task<int> DispatchAsync(Func<ComputeMaintenanceOutboxEntry, CancellationToken, Task> deliver, CancellationToken token)
    {
        var rows = await journal.ListMaintenanceEventsAsync(100, token);
        foreach (var row in rows)
        {
            await deliver(row, token);
            await journal.AcknowledgeMaintenanceEventAsync(row.Id, row.Digest, token);
        }
        return rows.Count;
    }
}
