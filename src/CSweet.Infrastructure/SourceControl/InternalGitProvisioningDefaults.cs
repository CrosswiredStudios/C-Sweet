using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.SourceControl;

public static class InternalGitProvisioningDefaults
{
    public static async Task<SourceControlConnection> EnsureAsync(CSweetDbContext db, Guid business, CancellationToken ct)
    {
        var connection = await db.SourceControlConnections.SingleOrDefaultAsync(c => c.OrganizationId == business && c.Provider == SourceControlProvider.InternalGit, ct);
        var now = DateTimeOffset.UtcNow;
        if (connection is null)
        {
            connection = new() { Id = Guid.NewGuid(), OrganizationId = business, Name = "C-Sweet internal Git", Provider = SourceControlProvider.InternalGit,
                Mode = SourceControlConnectionMode.InternalGit, ProviderAccountId = business.ToString("N"), AccountLogin = "C-Sweet", AccountType = "Business",
                Status = SourceControlConnectionStatus.Connected, CreatedAt = now, UpdatedAt = now };
            db.SourceControlConnections.Add(connection);
        }
        if (!await db.RepositoryProvisioningPolicies.AnyAsync(p => p.OrganizationId == business && p.ConnectionId == connection.Id, ct))
        {
            var template = new SourceControlRepositoryTemplate { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id,
                Name = "empty", DisplayName = "Empty internal repository", DefaultBranch = "main", CreatedAt = now, UpdatedAt = now };
            db.SourceControlRepositoryTemplates.Add(template);
            db.RepositoryProvisioningPolicies.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, ConnectionId = connection.Id,
                ApprovedTemplatesJson = JsonSerializer.Serialize(new[] { template.Id }), MaximumRepositories = 100,
                RequiresManagerApproval = false, CreatedAt = now, UpdatedAt = now });
        }
        await db.SaveChangesAsync(ct);
        return connection;
    }
}
