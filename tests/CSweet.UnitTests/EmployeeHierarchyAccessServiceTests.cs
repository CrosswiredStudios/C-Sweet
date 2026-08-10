using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class EmployeeHierarchyAccessServiceTests
{
    [Fact]
    public async Task AccessIncludesRecursiveDescendantsAndFailsClosedOnCycles()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var root = User(organizationId, "Root");
        var manager = User(organizationId, "Manager", root.Id);
        var report = User(organizationId, "Report", manager.Id);
        db.CoreOrganizationUsers.AddRange(root, manager, report);
        await db.SaveChangesAsync();
        var service = new EmployeeHierarchyAccessService(db);

        var accessible = await service.GetSelfAndDescendantsAsync(organizationId, root.Id);
        Assert.True(new HashSet<Guid>(accessible).SetEquals([root.Id, manager.Id, report.Id]));
        Assert.True(await service.CanAccessSensitiveAsync(organizationId, root.Id, report.Id));
        Assert.False(await service.CanAccessSensitiveAsync(organizationId, report.Id, root.Id));

        root.ReportsToOrganizationUserId = report.Id;
        await db.SaveChangesAsync();
        Assert.Empty(await service.GetSelfAndDescendantsAsync(organizationId, root.Id));
        Assert.False(await service.CanAccessSensitiveAsync(organizationId, root.Id, report.Id));
    }

    private static OrganizationUser User(Guid organizationId, string name, Guid? managerId = null) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = name,
        ReportsToOrganizationUserId = managerId, EmployeeType = EmployeeType.Human,
        IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };
}
