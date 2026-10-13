using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Retains project scope from an authorized read during one exact authenticated chat delivery.</summary>
public static class ProjectChatTurnAttribution
{
    public const string SourceKind = "ChatTurnProjectScope";

    public static async Task RecordAsync(CSweetDbContext db, Guid organization, Guid installation, Guid runtime,
        Guid project, DateTimeOffset now, CancellationToken token)
    {
        var active = await db.AgentWorkAttempts.AsNoTracking().Where(x => x.RuntimeInstanceId == runtime &&
            x.FinishedAt == null && x.LeaseExpiresAt > now && x.AgentWorkItem!.AgentInstallationId == installation &&
            x.AgentWorkItem.OrganizationId == organization.ToString()).Select(x => x.AgentWorkItemId).Take(2).ToArrayAsync(token);
        if (active.Length != 1) return; // Never guess between concurrent deliveries.
        var work = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == active[0], token);
        if (work.SourceType != "chat-turn" || !Guid.TryParse(work.SourceId, out var turn) ||
            !await db.ChatTurns.AnyAsync(x => x.Id == turn && x.OrganizationId == organization, token) ||
            !await db.Workstreams.AnyAsync(x => x.Id == project && x.OrganizationId == organization, token)) return;
        if (db.ProjectHealthSignals.Local.Any(x => x.OrganizationId == organization && x.WorkstreamId == project &&
                x.SourceKind == SourceKind && x.SourceId == turn) ||
            await db.ProjectHealthSignals.AnyAsync(x => x.OrganizationId == organization && x.WorkstreamId == project &&
                x.SourceKind == SourceKind && x.SourceId == turn, token)) return;
        db.ProjectHealthSignals.Add(new ProjectHealthSignal { Id = Guid.NewGuid(), OrganizationId = organization,
            WorkstreamId = project, SourceKind = SourceKind, SourceId = turn, OccurredAt = now,
            MeaningfulProgress = false, Detail = "An authorized project read was associated with this chat delivery." });
        await db.SaveChangesAsync(token);
    }
}
