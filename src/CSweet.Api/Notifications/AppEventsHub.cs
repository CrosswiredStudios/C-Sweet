using CSweet.Api.Auth;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Notifications;

public static class AppEventGroups
{
    public static string ApplicationUser(Guid id) => $"application-user:{id:D}";
    public static string OrganizationUser(Guid id) => $"organization-user:{id:D}";
    public static string CommunicationPerspective(Guid organizationId, Guid id) =>
        $"communication-perspective:{organizationId:D}:{id:D}";
}

public sealed class AppEventsHub(CSweetDbContext db) : Hub
{
    private const string PerspectiveGroupItemKey = "communication-perspective-group";

    public override async Task OnConnectedAsync()
    {
        var applicationUserId = Context.User?.GetApplicationUserId();
        if (!applicationUserId.HasValue)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AppEventGroups.ApplicationUser(applicationUserId.Value));
        var organizationUserIds = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.ApplicationUserId == applicationUserId && x.IsActive)
            .Select(x => x.Id)
            .ToListAsync(Context.ConnectionAborted);
        foreach (var organizationUserId in organizationUserIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, AppEventGroups.OrganizationUser(organizationUserId));

        await base.OnConnectedAsync();
    }

    public async Task SetCommunicationPerspective(Guid organizationId, Guid? agentOrganizationUserId)
    {
        var applicationUserId = Context.User?.GetApplicationUserId();
        if (!applicationUserId.HasValue)
            throw new HubException("An authenticated application user is required.");

        if (Context.Items.Remove(PerspectiveGroupItemKey, out var previousGroup) &&
            previousGroup is string previousGroupName)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, previousGroupName);

        if (!agentOrganizationUserId.HasValue) return;

        var activeHumanMember = await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId &&
            x.ApplicationUserId == applicationUserId &&
            x.EmployeeType == EmployeeType.Human &&
            x.IsActive,
            Context.ConnectionAborted);
        var activeAgent = activeHumanMember && await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
            x.Id == agentOrganizationUserId.Value &&
            x.OrganizationId == organizationId &&
            x.EmployeeType == EmployeeType.Agent &&
            x.IsActive,
            Context.ConnectionAborted);
        if (!activeAgent)
            throw new HubException("The requested communication perspective is not available.");

        var group = AppEventGroups.CommunicationPerspective(organizationId, agentOrganizationUserId.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        Context.Items[PerspectiveGroupItemKey] = group;
    }
}
