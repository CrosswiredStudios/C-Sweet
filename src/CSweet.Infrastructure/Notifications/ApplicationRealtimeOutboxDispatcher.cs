using System.Text.Json;
using CSweet.Application.Notifications;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Notifications;

public sealed class ApplicationRealtimeOutboxDispatcher(CSweetDbContext db) : IApplicationRealtimeOutboxDispatcher
{
    public async Task<int> DispatchBatchAsync(IApplicationRealtimePublisher publisher, int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var items = await db.ApplicationRealtimeOutbox
            .Where(x => x.Status == ApplicationRealtimeOutboxStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.Sequence)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToListAsync(cancellationToken);
        var published = 0;

        foreach (var item in items)
        {
            try
            {
                var recipients = await ResolveRecipientsAsync(item, cancellationToken);
                using var data = JsonDocument.Parse(item.DataJson);
                var envelope = new AppRealtimeEventEnvelope(item.Id, item.Sequence, item.EventType,
                    item.OrganizationId, item.Subject, item.OccurredAt, data.RootElement.Clone());
                await publisher.PublishAsync(new ApplicationRealtimePublication(envelope, recipients), cancellationToken);
                item.Status = ApplicationRealtimeOutboxStatus.Published;
                item.PublishedAt = DateTimeOffset.UtcNow;
                item.LastError = null;
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                item.Attempts++;
                item.LastError = exception.Message.Length <= 4096 ? exception.Message : exception.Message[..4096];
                item.Status = item.Attempts >= 20
                    ? ApplicationRealtimeOutboxStatus.DeadLettered
                    : ApplicationRealtimeOutboxStatus.Pending;
                item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(item.Attempts, 8))));
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return published;
    }

    private async Task<IReadOnlyList<Guid>> ResolveRecipientsAsync(ApplicationRealtimeOutboxItem item,
        CancellationToken cancellationToken)
    {
        if (item.RecipientOrganizationUserId.HasValue) return [item.RecipientOrganizationUserId.Value];
        var snapshot = DeserializeRecipients(item.RecipientOrganizationUserIdsJson);
        if (snapshot.Count > 0) return snapshot;
        if (item.ChatId.HasValue)
        {
            return await db.ConversationParticipants.AsNoTracking()
                .Where(x => x.ConversationId == item.ChatId && x.LeftAt == null)
                .Select(x => x.OrganizationUserId).Distinct().ToListAsync(cancellationToken);
        }
        return [];
    }

    private static IReadOnlyList<Guid> DeserializeRecipients(string json)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
