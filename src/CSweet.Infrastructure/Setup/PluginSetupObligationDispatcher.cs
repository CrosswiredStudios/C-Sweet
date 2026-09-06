using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>Converts durable setup obligations into deduplicated, exact-installation work.</summary>
public sealed class PluginSetupObligationDispatcher(CSweetDbContext db, AgentWorkInbox inbox, TimeProvider clock)
{
    public async Task DispatchAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var reminderBefore = now.AddHours(-24);
        var pending = await db.PluginSetupObligations.Where(x => x.CompletedAt == null && x.CancelledAt == null &&
                (x.IntroductionWorkId == null || (x.ReminderWorkId == null && x.CreatedAt <= reminderBefore)) &&
                db.AgentInstallations.Any(i => i.Id == x.InstallationId && i.IsEnabled))
            .OrderBy(x => x.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var obligation in pending)
        {
            var installation = await db.AgentInstallations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == obligation.InstallationId &&
                    x.BusinessId == obligation.OrganizationId.ToString(), ct);
            if (installation is null || installation.RevisionStatus != PluginRevisionStatus.Active)
            { obligation.CancelledAt = now; continue; }
            if (installation.SetupState == PluginSetupState.Ready)
            { obligation.CompletedAt = now; continue; }
            if (!installation.IsEnabled) continue;
            if (obligation.IntroductionWorkId is null)
                obligation.IntroductionWorkId = await EnqueueAsync(obligation, false, now, ct);
            if (now >= obligation.CreatedAt.AddHours(24) && obligation.ReminderWorkId is null)
            {
                obligation.ReminderWorkId = await EnqueueAsync(obligation, true, now, ct);
                obligation.ReminderQueuedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid> EnqueueAsync(PluginSetupObligation obligation, bool reminder,
        DateTimeOffset now, CancellationToken ct)
    {
        var eventId = reminder
            ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"setup-reminder:{obligation.Id:D}")).AsSpan(0, 16))
            : obligation.Id;
        // Stable payload and event identity survive a crash between inbox and obligation saves.
        var payload = JsonSerializer.SerializeToElement(new
        {
            organizationId = obligation.OrganizationId, installationId = obligation.InstallationId,
            agentOrganizationUserId = obligation.AgentOrganizationUserId,
            humanOrganizationUserId = obligation.HumanOrganizationUserId,
            conversationId = obligation.ConversationId,
            requestedAt = reminder ? obligation.CreatedAt.AddHours(24) : obligation.CreatedAt, reminder
        });
        var item = await inbox.EnqueueAsync(obligation.OrganizationId.ToString(), obligation.InstallationId,
            AgentWorkKind.Event, PluginSetupAssistancePolicy.RequestedEvent, payload,
            $"plugin-setup:{eventId:D}", now.AddHours(24), correlationId: obligation.Id.ToString(),
            sourceType: "plugin-setup-assistance", sourceId: eventId.ToString(), cancellationToken: ct);
        return item.Id;
    }
}
