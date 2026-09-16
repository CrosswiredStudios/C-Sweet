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

namespace CSweet.UnitTests;

public sealed class PersonalTodoPostgresTests
{
    [PersonalTodoPostgresFact]
    public async Task FreshHireCanReadItsQueueAndReconciliationRevokesInactiveOwnerGrants()
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
