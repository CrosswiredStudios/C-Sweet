using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Domain.Notifications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class BusinessSuggestionService(CSweetDbContext db)
{
    public async Task EnsureChiefAsync(Guid org, CancellationToken ct)
    {
        if (db.BusinessSuggestions.Local.Any(x => x.OrganizationId == org && x.Key == "hire-chief-of-staff") ||
            await db.BusinessSuggestions.AnyAsync(x => x.OrganizationId == org && x.Key == "hire-chief-of-staff", ct)) return;
        if (await db.LeadershipAssignments.AnyAsync(x => x.OrganizationId == org && x.PositionKey == "chief-of-staff" && x.EndsAt == null && x.StartsAt <= DateTimeOffset.UtcNow &&
            db.CoreOrganizationUsers.Any(u => u.Id == x.OrganizationUserId && u.IsActive && u.ArchivedAt == null), ct)) return;
        var now = DateTimeOffset.UtcNow;
        db.BusinessSuggestions.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, CreatedAt = now, UpdatedAt = now });
    }

    public async Task<IReadOnlyList<BusinessSuggestionResponse>> ListAsync(Guid org, Guid user, CancellationToken ct)
    {
        await RequireActorAsync(org, user, ct);
        return (await db.BusinessSuggestions.AsNoTracking().Where(x => x.OrganizationId == org && x.Status == "Pending")
            .OrderBy(x => x.CreatedAt).ToListAsync(ct)).Select(x => new BusinessSuggestionResponse(x.Id, x.Kind, x.Status,
                x.Revision, "Hire a Chief of Staff", "A Chief of Staff can help assess your business and organize its next steps.",
                $"/organizations/{org:D}/employees/marketplace?role=Chief%20of%20Staff") { Key = x.Key, DismissedByOrganizationUserId = x.DismissedByOrganizationUserId, DismissedAt = x.DismissedAt }).ToArray();
    }

    public async Task<IReadOnlyList<ImportedProjectAttentionResponse>> ImportedProjectsAsync(Guid org, Guid user, CancellationToken ct)
    {
        await RequireActorAsync(org, user, ct);
        return await (from association in db.ProjectRepositoryAssociations.AsNoTracking()
                      join profile in db.ImportedProjectProfiles.AsNoTracking() on association.ProjectId equals profile.ProjectId
                      join project in db.Workstreams.AsNoTracking() on association.ProjectId equals project.Id
                      join source in db.RepositoryOnboardingSources.AsNoTracking() on association.ImportSourceId equals source.Id
                      where project.OrganizationId == org
                      orderby project.UpdatedAt descending
                      select new ImportedProjectAttentionResponse(project.Id, project.Name, profile.Status,
                          $"/organizations/{org:D}/projects/{project.Id:D}", $"/organizations/{org:D}/onboarding?draft={source.DraftId:D}")).Take(50).ToListAsync(ct);
    }

    public async Task DismissAsync(Guid org, Guid user, Guid id, long revision, CancellationToken ct)
    {
        var actor = await RequireActorAsync(org, user, ct);
        var item = await db.BusinessSuggestions.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == id, ct)
            ?? throw new KeyNotFoundException("Suggestion not found.");
        if (item.Status != "Pending") return;
        if (item.Revision != revision) throw new DbUpdateConcurrencyException("The suggestion changed. Refresh before continuing.");
        item.Status = "Dismissed"; item.DismissedByOrganizationUserId = actor.Id;
        item.DismissedAt = item.UpdatedAt = DateTimeOffset.UtcNow; item.Revision++;
        await db.SaveChangesAsync(ct);
    }

    private async Task<OrganizationUser> RequireActorAsync(Guid org, Guid user, CancellationToken ct) =>
        await db.CoreOrganizationUsers.SingleOrDefaultAsync(x => x.OrganizationId == org && x.ApplicationUserId == user &&
            x.IsActive && x.ArchivedAt == null && x.EmployeeType == EmployeeType.Human && x.PermissionLevel >= OrganizationPermissionLevel.Manager, ct)
        ?? throw new UnauthorizedAccessException("Business owner or manager access is required.");

    internal static void Queue(CSweetDbContext db, Guid org, string type, Guid id)
    {
        var now = DateTimeOffset.UtcNow;
        if (type == AppRealtimeEvents.ProjectImportChanged)
        {
            var context = new CSweet.WorkManagement.Contracts.AgentWorkContext(org, id, null, null, null, null, null, Guid.NewGuid(), null, null);
            var eventId = Guid.NewGuid();
            db.AgentPlatformEventOutbox.Add(new CSweet.Domain.Core.AgentPlatformEventOutboxItem
            {
                Id = eventId, OrganizationId = org, EventType = "com.csweet.workstream.changed.v1",
                DataJson = JsonSerializer.Serialize(new CSweet.WorkManagement.Contracts.GenericResourceEvent(eventId, now, context,
                    "Workstream", id, 0, "Imported project context changed", "Changed", JsonSerializer.SerializeToElement(new { projectId = id }))),
                IdempotencyKey = $"project-import:{eventId:N}", NextAttemptAt = now, OccurredAt = now
            });
        }
        db.ApplicationRealtimeOutbox.Add(new ApplicationRealtimeOutboxItem { Id = Guid.NewGuid(), OrganizationId = org,
            EventType = type, Subject = $"business/{org:D}/{id:D}", DataJson = JsonSerializer.Serialize(new { resourceId = id }),
            Status = ApplicationRealtimeOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
    }
}
