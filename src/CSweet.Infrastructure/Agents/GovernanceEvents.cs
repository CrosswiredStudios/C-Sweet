using System.Text.Json;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Agents;

public static class GovernanceEvents
{
    public const string Changed = CSweet.Contracts.Realtime.AppRealtimeEvents.AgentGovernanceChanged;
    /// <summary>Agent-facing wake hint; a governance change wakes the agent even without a subscription.</summary>
    public const string AgentWake = "platform.agent-governance.changed.v1";
    public static async Task EnqueueAsync(CSweetDbContext db, Guid organization, Guid installation, Guid subject,
        string state, bool wakeAgent, CancellationToken token, Guid? humanActor = null)
    {
        var now = DateTimeOffset.UtcNow;
        db.QueueAudit(new CSweet.Application.Setup.AuditEventWriteRequest("agents.governance.changed",
            OrganizationId: organization == Guid.Empty ? null : organization, EntityType: "AgentGovernance",
            EntityId: subject, Summary: $"Agent governance recorded as {state}.",
            MetadataJson: JsonSerializer.Serialize(new { installationId = installation, state }),
            Actor: new CSweet.Application.Setup.AuditActor(humanActor.HasValue ? "Human" : "Platform", ApplicationUserId: humanActor)));
        var recipients = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.IsActive && x.ArchivedAt == null && x.PermissionLevel >= CSweet.Domain.Core.OrganizationPermissionLevel.Manager)
            .Select(x => x.Id).ToArrayAsync(token);
        var personalOwner = organization == Guid.Empty ? await db.AgentInstallations.AsNoTracking()
            .Where(x => x.Id == installation && x.Scope == CSweet.Domain.Setup.PluginInstallationScope.User)
            .Select(x => x.OwnerApplicationUserId).SingleOrDefaultAsync(token) : null;
        var data = JsonSerializer.Serialize(new { organizationId = organization, installationId = installation, id = subject, state, recoveryCapability = "platform.agent-governance.read.v1" });
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem {
            Id = Guid.NewGuid(), OrganizationId = organization == Guid.Empty ? null : organization, RecipientApplicationUserId = personalOwner, RecipientOrganizationUserIdsJson = JsonSerializer.Serialize(recipients),
            EventType = Changed, Subject = subject.ToString("D"), DataJson = data, OccurredAt = now, NextAttemptAt = now });
        if (wakeAgent && organization != Guid.Empty)
            db.AgentPlatformEventOutbox.Add(new() { Id = Guid.NewGuid(), OrganizationId = organization,
                TargetInstallationId = installation, EventType = AgentWake, DataJson = data,
                IdempotencyKey = $"governance:{subject:N}:{state}", OccurredAt = now, NextAttemptAt = now });
    }
}
