using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class PersonalTodoPostgresTests
{
    [PersonalTodoPostgresFact]
    public async Task FreshHireCanClaimAndCompleteWithoutWaitRecoveryInterferenceAndInactiveGrantsAreRevoked()
    {
        var connection = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("CSWEET_PERSONAL_TODO_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        // Always create a unique test database; never migrate or clear the supplied database.
        connection.Database = "personal_todo_test_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var organization = new Organization { Id = Guid.NewGuid(), Name = "Fresh business" };
            var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
                DisplayName = "Owner", EmployeeType = EmployeeType.Human,
                PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/chief" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id,
                AgentId = "chief", Version = "1.0.0", ManifestJson = "{}" };
            var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = package.Id,
                InstallationKey = Guid.NewGuid(), BusinessId = organization.Id.ToString() };
            var chief = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization.Id,
                DisplayName = "Chief", EmployeeType = EmployeeType.Agent, IsActive = true,
                ReportsToOrganizationUserId = owner.Id, AgentInstallationId = installation.Id };
            db.AddRange(organization, owner, source, package, installation, chief);
            await db.SaveChangesAsync();
            var service = new PersonalTodoService(db, TimeProvider.System);
            var actor = new PersonalTodoActor(chief.Id, installation.Id);

            var directory = await service.ListAsync(organization.Id, actor);
            var board = Assert.Single(directory.Boards);
            Assert.Equal(chief.Id, board.OwnerOrganizationUserId);
            Assert.Empty(board.Items);
            var boardId = await db.WorkBoards.Where(x => x.OwnerOrganizationUserId == chief.Id)
                .Select(x => x.Id).SingleAsync();
            Assert.True(await db.ScopedActionGrants.AnyAsync(x => x.ScopeId == boardId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == installation.Id &&
                x.Action == PersonalTodoActions.Read && x.RevokedAt == null));

            await service.ReconcileAsync();
            var grants = await db.ScopedActionGrants.CountAsync();
            db.ChangeTracker.Clear();
            await service.ListAsync(organization.Id, actor);
            await service.ReconcileAsync();
            Assert.Equal(grants, await db.ScopedActionGrants.CountAsync());
            Assert.Equal(2, await db.WorkBoards.CountAsync(x => x.Kind == WorkBoardKind.Personal));

            var item = await service.AddAsync(organization.Id, actor,
                new("Resume planning", null, Wire.WorkPriorities.Medium, null, "resume-planning"));
            var task = await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id);
            // Reproduce legacy Ready work with a due review left behind by a coordination wake.
            task.NextReviewAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            task.WaitingReason = "Previous coordination wait";
            task.WaitingOnOrganizationUserId = owner.Id;
            await db.SaveChangesAsync();
            var eventId = Guid.NewGuid();
            var claim = await service.ClaimAsync(organization.Id, actor,
                new(eventId, "claim-resumed-planning") { ItemId = item.Id, ExpectedRevision = item.Revision });
            Assert.NotNull(claim.Item);
            Assert.Equal(Wire.PersonalTodoStatuses.Running, claim.Item.Status);
            await db.Entry(task).ReloadAsync();
            Assert.Null(task.NextReviewAt);
            Assert.Null(task.WaitingReason);
            Assert.Null(task.WaitingOnOrganizationUserId);
            Assert.Equal(eventId, task.ClaimEventId);
            var claimedRevision = task.Revision;

            await service.ReconcileAsync();
            await db.Entry(task).ReloadAsync();
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.Equal(claimedRevision, task.Revision);
            var completed = await service.CompleteAsync(organization.Id, actor,
                new(item.Id, eventId, claimedRevision, "Planning resumed", "complete-resumed-planning"));
            Assert.Equal(Wire.PersonalTodoStatuses.Completed, completed.Status);

            var employee = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == chief.Id);
            employee.IsActive = false;
            await db.SaveChangesAsync();
            await service.ReconcileAsync();
            Assert.False(await db.ScopedActionGrants.AnyAsync(x => x.ScopeId == boardId && x.RevokedAt == null));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class PersonalTodoPostgresFactAttribute : FactAttribute
    {
        public PersonalTodoPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_PERSONAL_TODO_TEST_POSTGRES")))
                Skip = "Set CSWEET_PERSONAL_TODO_TEST_POSTGRES to a loopback PostgreSQL server.";
        }
    }
}
