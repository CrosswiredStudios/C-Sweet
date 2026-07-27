using System.Text.Json;
using CSweet.Application.Communications;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

public sealed class CommunicationEventOutboxDispatcher(CSweetDbContext db) : ICommunicationEventOutboxDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> DispatchBatchAsync(
        ICommunicationEventPublisher publisher,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var events = await db.CommunicationEventOutbox
            .Where(x => x.Status == CommunicationEventOutboxStatus.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.Sequence)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToListAsync(cancellationToken);
        var published = 0;

        foreach (var item in events)
        {
            var delivered = DeserializeIds(item.DeliveredInstallationIdsJson);
            try
            {
                var targets = await ResolveTargetsAsync(item.OrganizationId, item.EventType, cancellationToken);
                using var data = JsonDocument.Parse(item.DataJson);
                var envelope = new CommunicationEventEnvelope(item.Id, item.OrganizationId, item.Sequence,
                    item.EventType, item.Subject, item.OccurredAt, data.RootElement.Clone());

                foreach (var target in targets.Where(x => !delivered.Contains(x)))
                {
                    await publisher.PublishAsync(new CommunicationEventPublication(
                        item.EventType, item.Subject, envelope, target), cancellationToken);
                    delivered.Add(target);
                }

                item.DeliveredInstallationIdsJson = JsonSerializer.Serialize(delivered, JsonOptions);
                item.Status = CommunicationEventOutboxStatus.Published;
                item.PublishedAt = DateTimeOffset.UtcNow;
                item.LastError = null;
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                item.Attempts++;
                item.DeliveredInstallationIdsJson = JsonSerializer.Serialize(delivered, JsonOptions);
                item.LastError = exception.Message.Length <= 4096 ? exception.Message : exception.Message[..4096];
                item.Status = item.Attempts >= 20
                    ? CommunicationEventOutboxStatus.DeadLettered
                    : CommunicationEventOutboxStatus.Pending;
                var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(item.Attempts, 8)));
                item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return published;
    }

    private async Task<IReadOnlyList<Guid>> ResolveTargetsAsync(
        Guid organizationId,
        string eventType,
        CancellationToken cancellationToken)
    {
        var organizationBusinessId = organizationId.ToString("D");
        var installations = await db.AgentInstallations.AsNoTracking()
            .Where(x => x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active &&
                ((x.Scope == PluginInstallationScope.Organization && x.BusinessId == organizationBusinessId) ||
                 (x.Scope == PluginInstallationScope.System && db.PluginOrganizationGrants.Any(g =>
                     g.PluginInstallationId == x.Id && g.OrganizationId == organizationId))))
            .Include(x => x.Grant)
            .ToListAsync(cancellationToken);

        return installations
            .Where(x => DeserializeStrings(x.Grant?.EventSubscriptionsJson).Contains(eventType))
            .Select(x => x.Id)
            .Distinct()
            .ToList();
    }

    private static HashSet<Guid> DeserializeIds(string json)
    {
        try { return JsonSerializer.Deserialize<HashSet<Guid>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static HashSet<string> DeserializeStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<HashSet<string>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
}
