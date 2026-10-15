using System.Security.Cryptography;
using System.Text;
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
        Guid eventId,
        string idempotencyKey,
        Guid? exactInstallationId = null,
        bool requireSubscription = true,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var result = await EnqueueEventWithRecipientsAsync(
            organizationId,
            eventName,
            payload,
            eventId,
            idempotencyKey,
            exactInstallationId,
            requireSubscription,
            deadline,
            cancellationToken);
        return result.DeliveryCount;
    }

    public async Task<AgentEventRoutingResult> EnqueueEventWithRecipientsAsync(
        string organizationId,
        string eventName,
        JsonElement payload,
        Guid eventId,
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
        var recipients = new List<Guid>();
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
                DeliveryKey(idempotencyKey, installation.Id),
                deadline ?? timeProvider.GetUtcNow().AddHours(1),
                // Event keys can occupy all 200 outbox characters. The inbox correlation
                // column allows 128; retain the source event identity when the key is longer.
                correlationId: idempotencyKey.Length <= 128 ? idempotencyKey : $"platform-event:{eventId:D}",
                sourceType: "platform-event",
                sourceId: eventId.ToString("D"),
                maximumAttempts: 3,
                cancellationToken: cancellationToken);
            recipients.Add(installation.Id);
        }
        return new AgentEventRoutingResult(recipients);
    }

    private static string DeliveryKey(string eventKey, Guid installationId)
    {
        var key = $"{eventKey}:{installationId:D}";
        // Preserve every previously valid delivery key so replay finds existing inbox work.
        // Hash the complete key (including recipient) rather than truncating shared prefixes.
        return key.Length <= 200 ? key :
            $"platform-event-sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
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

public sealed record AgentEventRoutingResult(IReadOnlyList<Guid> RecipientInstallationIds)
{
    public int DeliveryCount => RecipientInstallationIds.Count;
}
