using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Chat;

internal static class ChatReportingAuthority
{
    internal const string AncestorKey = "senderIsReportingAncestor";
    internal const string CurrentMessageKey = "currentUserMessage";

    internal static async Task<bool> IsAncestorAsync(CSweetDbContext db, Guid organizationId,
        Guid targetId, Guid senderId, CancellationToken token)
    {
        if (senderId == targetId) return false;
        var seen = new HashSet<Guid>();
        var current = targetId;
        for (var depth = 0; depth < 32 && seen.Add(current); depth++)
        {
            var employee = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == current && x.OrganizationId == organizationId && x.IsActive && x.ArchivedAt == null, token);
            if (employee is null) return false;
            if (current == senderId) return true;
            if (employee.ReportsToOrganizationUserId is not { } manager) return false;
            current = manager;
        }
        return false;
    }
}
