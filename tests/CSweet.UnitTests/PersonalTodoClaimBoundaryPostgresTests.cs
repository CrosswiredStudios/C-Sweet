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

public sealed class PersonalTodoClaimBoundaryPostgresTests
{
    [BoundaryPostgresTheory]
    [InlineData("ready")]
    [InlineData("replayed-claim")]
    [InlineData("expired-claim")]
    [InlineData("revoked-grant")]
    public async Task ProjectStageTicketsCannotEnterOrBeResetByThePersonalDispatcher(string scenario)
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_PERSONAL_TODO_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "personal_boundary_test_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow; var org = new Organization { Id = Guid.NewGuid(), Name = "Boundary fixture" };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/developer" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "developer", Version = "1.0.0", ManifestJson = "{}" };
            var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersionId = package.Id, InstallationKey = Guid.NewGuid(), BusinessId = org.Id.ToString() };
            var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Manager", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true };
            var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = org.Id, DisplayName = "Developer", EmployeeType = EmployeeType.Agent, IsActive = true, AgentInstallationId = installation.Id, ReportsToOrganizationUserId = manager.Id };
            var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = org.Id, Name = "Project", Status = WorkstreamStatus.Active, AccountableManagerOrganizationUserId = manager.Id };
            var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org.Id, WorkstreamId = project.Id, Name = "Project board", Key = "boundary", Kind = WorkBoardKind.Standard, ManagerOrganizationUserId = manager.Id };
            foreach (var category in Enum.GetValues<WorkBoardColumnCategory>())
                board.Columns.Add(new() { Id = Guid.NewGuid(), BoardId = board.Id, Name = category.ToString(), Category = category, Position = (int)category });
            var grant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = org.Id, SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installation.Id, Action = PersonalTodoActions.Claim, ScopeKind = GrantScopeKind.Board, ScopeId = board.Id,
                GrantedAt = now, RevokedAt = scenario == "revoked-grant" ? now : null };
            var eventId = Guid.NewGuid();
            var canonical = Item("Canonical stage", "canonical", context: null);
            canonical.StageAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = org.Id, BoardId = board.Id, WorkItemId = canonical.Id,
                StageKey = "development", PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation, AgentInstallationId = installation.Id });
            var personal = Item("Retained personal plan", "personal", context: "{}");
            if (scenario is "replayed-claim" or "expired-claim")
            {
                canonical.Status = WorkTaskStatus.Running;
                canonical.BoardColumnId = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.InProgress).Id;
                canonical.ClaimEventId = eventId;
                canonical.ClaimExpiresAt = now.AddMinutes(scenario == "expired-claim" ? -1 : 5);
            }
            db.AddRange(org, source, package, installation, manager, employee, project, board, grant, canonical, personal);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var before = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == canonical.Id);
            var service = new PersonalTodoService(db, TimeProvider.System);
            var selected = scenario == "expired-claim" ? personal : canonical;
            var request = new Wire.ClaimPersonalTodoItemRequest(eventId, "boundary-claim") { ItemId = selected.Id, ExpectedRevision = selected.Revision };
            if (scenario == "revoked-grant")
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ClaimAsync(org.Id, new(employee.Id, installation.Id), request));
            else
            {
                var claim = await service.ClaimAsync(org.Id, new(employee.Id, installation.Id), request);
                if (scenario == "expired-claim") Assert.Equal(personal.Id, Assert.IsType<Wire.PersonalTodoItem>(claim.Item).Id);
                else Assert.Null(claim.Item);
            }
            var retained = await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == canonical.Id);
            Assert.Equal(before.Status, retained.Status); Assert.Equal(before.Revision, retained.Revision);
            Assert.Equal(before.ClaimEventId, retained.ClaimEventId); Assert.Equal(before.ClaimExpiresAt, retained.ClaimExpiresAt);

            WorkTask Item(string title, string key, string? context) => new()
            {
                Id = Guid.NewGuid(), OrganizationId = org.Id, BoardId = board.Id,
                BoardColumnId = board.Columns.Single(x => x.Category == WorkBoardColumnCategory.ToDo).Id,
                Title = title, Kind = WorkItemKind.Task, TypeKey = Wire.WorkItemTypeKeys.GeneralTaskV1, Status = WorkTaskStatus.Ready,
                AssignedEmployeeId = employee.Id, AssignedAgentInstallationId = installation.Id, PersonalWorkContextJson = context,
                CreationIdempotencyKey = key, CreatedAt = now, UpdatedAt = now
            };
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private sealed class BoundaryPostgresTheoryAttribute : TheoryAttribute
    {
        public BoundaryPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_PERSONAL_TODO_TEST_POSTGRES")))
                Skip = "Set CSWEET_PERSONAL_TODO_TEST_POSTGRES to a loopback PostgreSQL fixture.";
        }
    }
}
