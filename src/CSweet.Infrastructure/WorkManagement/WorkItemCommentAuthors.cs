using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.WorkManagement;

public static class WorkItemCommentAuthors
{
    public static async Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        CSweetDbContext db,
        Guid organizationId,
        IEnumerable<WorkItemComment> comments,
        CancellationToken cancellationToken)
    {
        var installations = comments
            .Where(c => c.AuthorKind == GrantSubjectKind.AgentInstallation)
            .Select(c => c.AuthorSubjectId).Distinct().ToArray();
        if (installations.Length == 0) return new Dictionary<Guid, string>();

        var employees = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.IsActive &&
                u.AgentInstallationId != null && installations.Contains(u.AgentInstallationId.Value))
            .Select(u => new { InstallationId = u.AgentInstallationId!.Value, u.DisplayName })
            .ToListAsync(cancellationToken);
        return employees.Where(u => !string.IsNullOrWhiteSpace(u.DisplayName))
            .ToDictionary(u => u.InstallationId, u => u.DisplayName);
    }

    // Stored names remain audit snapshots; presentation follows the installation's employee identity.
    public static string DisplayName(
        WorkItemComment comment,
        IReadOnlyDictionary<Guid, string> names) =>
        comment.AuthorKind == GrantSubjectKind.AgentInstallation &&
        names.TryGetValue(comment.AuthorSubjectId, out var name)
            ? name : comment.AuthorDisplayName;
}
