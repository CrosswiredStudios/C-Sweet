using CSweet.Application.Security;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class WorkBoardGrantServiceTests
{
    [Fact]
    public async Task OwnerCanGrantAgentOnlyDelegableBoardActionsAndReplaceThem()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var boardId = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization
        {
            Id = organizationId,
            Name = "Test company",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = ownerId,
            OrganizationId = organizationId,
            ApplicationUserId = applicationUserId,
            DisplayName = "Owner",
            EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.WorkBoards.Add(new WorkBoard
        {
            Id = boardId,
            OrganizationId = organizationId,
            Name = "Delivery",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId,
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = organizationId.ToString("D"),
            RevisionStatus = PluginRevisionStatus.Active,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        var service = new WorkBoardGrantService(
            db, authorization, new TestAuditEventWriter());

        var granted = await service.SetSubjectGrantsAsync(
            organizationId,
            boardId,
            applicationUserId,
            new SetWorkBoardSubjectGrantsRequest(
                "AgentInstallation",
                installationId,
                [WorkBoardActions.Read, WorkItemActions.Read, WorkItemActions.Create]));

        Assert.Equal(3, granted.Count);
        Assert.All(granted, grant => Assert.False(grant.CanDelegate));
        var persisted = await db.ScopedActionGrants
            .Where(x =>
                x.SubjectKind == GrantSubjectKind.AgentInstallation &&
                x.SubjectId == installationId &&
                x.RevokedAt == null)
            .ToListAsync();
        Assert.All(persisted, grant => Assert.NotNull(grant.ParentGrantId));

        var replaced = await service.SetSubjectGrantsAsync(
            organizationId,
            boardId,
            applicationUserId,
            new SetWorkBoardSubjectGrantsRequest(
                "AgentInstallation",
                installationId,
                [WorkBoardActions.Read]));

        Assert.Equal(WorkBoardActions.Read, Assert.Single(replaced).Action);
        Assert.Equal(3, await db.ScopedActionGrants.CountAsync(x =>
            x.SubjectKind == GrantSubjectKind.AgentInstallation &&
            x.SubjectId == installationId &&
            x.RevokedAt != null));

        var organizationGrant = await service.SetOrganizationSubjectGrantsAsync(
            organizationId,
            applicationUserId,
            new SetWorkBoardSubjectGrantsRequest(
                "AgentInstallation",
                installationId,
                [WorkBoardActions.Create]));
        Assert.Equal(WorkBoardActions.Create, Assert.Single(organizationGrant).Action);
        Assert.Equal(GrantScopeKind.Organization, await db.ScopedActionGrants
            .Where(x => x.Id == organizationGrant[0].Id)
            .Select(x => x.ScopeKind)
            .SingleAsync());
    }

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}
