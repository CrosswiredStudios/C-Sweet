using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class AgentWorkRouter(
    CSweetDbContext db,
    AgentWorkInbox inbox,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> EnqueueEventAsync(
        string organizationId,
        string eventName,
        JsonElement payload,
        string idempotencyKey,
        Guid? exactInstallationId = null,
        bool requireSubscription = true,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var installations = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.Grant)
            .Where(x => x.BusinessId == organizationId &&
                        x.IsEnabled &&
                        x.RevisionStatus == PluginRevisionStatus.Active &&
                        (exactInstallationId == null || x.Id == exactInstallationId))
            .ToListAsync(cancellationToken);
        var count = 0;
        foreach (var installation in installations)
        {
            if (requireSubscription && !Subscriptions(installation.Grant).Contains(eventName))
                continue;
            await inbox.EnqueueAsync(
                organizationId,
                installation.Id,
                AgentWorkKind.Event,
                eventName,
                payload,
                $"{idempotencyKey}:{installation.Id:D}",
                deadline ?? timeProvider.GetUtcNow().AddHours(1),
                correlationId: idempotencyKey,
                sourceType: "platform-event",
                maximumAttempts: 3,
                cancellationToken: cancellationToken);
            count++;
        }
        return count;
    }

    private static IReadOnlySet<string> Subscriptions(AgentInstallationGrant? grant)
    {
        if (grant is null)
            return new HashSet<string>();
        var json = grant.EventSubscriptionsJson;
        try
        {
            return (JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>();
        }
    }
}
