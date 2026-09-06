using CSweet.Contracts.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public sealed partial class InternalRepositoryManagementService
{
    public async Task<SourceControlActivityPage> ActivityAsync(Guid business, Guid user, Guid? repositoryId,
        string? outcome, long? beforeSequence, int pageSize, CancellationToken ct)
    {
        await AuthorizeAsync(business, user, true, ct);
        if (pageSize is < 1 or > 100 || beforeSequence is <= 0 || repositoryId == Guid.Empty)
            throw new ArgumentException("Choose a valid activity page and repository.");
        if (outcome is not (null or "Started" or "Completed" or "Failed"))
            throw new ArgumentException("Choose a supported activity outcome.");

        // Query the ledger directly so deleted repositories retain inspectable history.
        // Never return payloads, metadata, transport errors, or credential material in this view.
        var query = db.AuditEvents.AsNoTracking().Where(e => e.OrganizationId == business && e.Category == "SourceControl");
        if (repositoryId.HasValue) query = query.Where(e => e.EntityType == "SourceControlRepository" && e.EntityId == repositoryId);
        if (outcome is not null) query = query.Where(e => e.Outcome == outcome);
        if (beforeSequence.HasValue) query = query.Where(e => e.Sequence < beforeSequence);
        var entries = await query.OrderByDescending(e => e.Sequence).Take(pageSize + 1)
            .Select(e => new SourceControlActivityEntry(e.Id, e.Sequence, e.OccurredAt, e.EventType, e.Outcome,
                e.EntityType, e.EntityId, e.ActorKind, e.ActorDisplayName, e.ActorApplicationUserId, e.ActorInstallationId, e.TraceId))
            .ToListAsync(ct);
        var hasMore = entries.Count > pageSize;
        if (hasMore) entries.RemoveAt(pageSize);
        return new(entries, hasMore ? entries[^1].Sequence : null);
    }
}
