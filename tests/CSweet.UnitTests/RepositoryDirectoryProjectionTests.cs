using System.Text.Json;
using CSweet.Contracts.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.SourceControl;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class RepositoryDirectoryProjectionTests
{
    [Fact]
    public async Task DirectoryResolvesProjectsAndDeduplicatesOnlyCurrentBusinessAccess()
    {
        await using var db = Database();
        var business = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = business, Name = "web", CreatedAt = DateTimeOffset.UtcNow.AddDays(-5) };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = business, Name = "Customer portal" };
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = business, Name = "Delivery" };
        var revokedTeam = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = business, Name = "Revoked team" };
        var archivedTeam = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = business, Name = "Archived team", ArchivedAt = DateTimeOffset.UtcNow };
        var agent = Agent(db, business, "Current agent");
        var disabled = Agent(db, business, "Disabled agent", false);
        var ended = Agent(db, business, "Former member");
        var revoked = Agent(db, business, "Revoked access");
        var archived = Agent(db, business, "Archived team agent");
        var outsider = Agent(db, foreign, "Other business agent");
        var reader = Human(business, "Reader", OrganizationPermissionLevel.Viewer);
        var owner = Human(business, "Owner", OrganizationPermissionLevel.Owner);
        var former = Human(business, "Former owner", OrganizationPermissionLevel.Owner); former.IsActive = false;
        db.AddRange(repository, project, team, revokedTeam, archivedTeam, reader, owner, former, Human(foreign, "Foreign owner", OrganizationPermissionLevel.Owner));
        db.RepositoryProvisioningRequests.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, WorkstreamId = project.Id });
        db.TeamRepositoryPolicies.AddRange(
            new() { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, TeamId = team.Id },
            new() { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, TeamId = revokedTeam.Id, DisabledAt = DateTimeOffset.UtcNow },
            new() { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id, TeamId = archivedTeam.Id });
        foreach (var employee in new[] { agent, agent, disabled, outsider })
            db.TeamMemberships.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = team.Id, OrganizationUserId = employee.Id });
        db.TeamMemberships.AddRange(
            new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = team.Id, OrganizationUserId = ended.Id, EndedAt = DateTimeOffset.UtcNow },
            new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = revokedTeam.Id, OrganizationUserId = revoked.Id },
            new() { Id = Guid.NewGuid(), OrganizationId = business, TeamId = archivedTeam.Id, OrganizationUserId = archived.Id });
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = business, WorkstreamId = project.Id };
        db.Add(board);
        db.CoreWorkTasks.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, BoardId = board.Id,
            DevelopmentBriefJson = JsonSerializer.Serialize(new { RepositoryId = repository.Id }) });
        var foreignProject = new Workstream { Id = Guid.NewGuid(), OrganizationId = foreign, Name = "Secret project" };
        db.Add(foreignProject);
        db.RepositoryProvisioningRequests.Add(new() { Id = Guid.NewGuid(), OrganizationId = foreign, RepositoryId = repository.Id, WorkstreamId = foreignProject.Id });
        await db.SaveChangesAsync();
        var result = Assert.Single(await RepositoryDirectoryProjection.PopulateAsync(db, business, [Summary(repository)], default));
        Assert.Equal(repository.CreatedAt, result.CreatedAt);
        Assert.Equal(project.Id, Assert.Single(result.Projects!).Id);
        Assert.Equal(3, result.Access!.Count);
        Assert.Equal("Current agent", Assert.Single(result.Access, a => a.EmployeeType == "Agent").DisplayName);
        Assert.Equal("Team access", result.Access.Single(a => a.OrganizationUserId == agent.Id).AccessType);
        Assert.Equal("Read", result.Access.Single(a => a.OrganizationUserId == reader.Id).AccessType);
        Assert.Equal("Admin", result.Access.Single(a => a.OrganizationUserId == owner.Id).AccessType);
    }

    [Fact]
    public async Task DirectoryChoosesLatestPersistedActivityAndResolvesActorWithoutExposingPayloads()
    {
        await using var db = Database();
        var business = Guid.NewGuid();
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = business, Name = "web" };
        var author = Agent(db, business, "Maya Chen");
        var human = Human(business, "Jamie Davis", OrganizationPermissionLevel.Owner);
        var now = DateTimeOffset.UtcNow;
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id,
            AgentInstallationId = author.AgentInstallationId!.Value, UpdatedAt = now.AddHours(-2), Status = SourceControlWorkspaceStatus.Ready };
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = business, RepositoryId = repository.Id,
            WorkspaceId = workspace.Id, UpdatedAt = now.AddHours(-1), Status = SourceControlPublicationStatus.Merged };
        db.AddRange(repository, workspace, publication, human);
        var audit = new AuditEvent { Id = Guid.NewGuid(), OrganizationId = business, Sequence = 1, EntityType = "SourceControlRepository",
            EntityId = repository.Id, Category = "SourceControl", EventType = "SourceControl.Repository.Create", OccurredAt = now.AddDays(-1),
            ActorApplicationUserId = human.ApplicationUserId, MetadataJson = "sensitive payload" };
        db.Add(audit);
        db.AuditEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Sequence = 100, EntityType = "SourceControlRepository",
            EntityId = repository.Id, Category = "SourceControl", EventType = "Foreign event", OccurredAt = now });
        await db.SaveChangesAsync();
        var result = Assert.Single(await RepositoryDirectoryProjection.PopulateAsync(db, business, [Summary(repository)], default));
        Assert.Equal("SourceControl.Publication.Merged", result.LastEventType);
        Assert.Equal("Maya Chen", result.LastEventActor);
        Assert.Equal(publication.UpdatedAt, result.LastEventOccurredAt);
        db.AuditEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, Sequence = 2, EntityType = "SourceControlRepository",
            EntityId = repository.Id, Category = "SourceControl", EventType = "SourceControl.Repository.Update", OccurredAt = now,
            ActorApplicationUserId = human.ApplicationUserId, MetadataJson = "sensitive payload" });
        await db.SaveChangesAsync();
        result = Assert.Single(await RepositoryDirectoryProjection.PopulateAsync(db, business, [Summary(repository)], default));
        Assert.Equal("Jamie Davis", result.LastEventActor);
        Assert.Equal(now, result.LastEventOccurredAt);
        Assert.DoesNotContain("sensitive payload", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task UnassignedRepositoryToleratesMissingOrMalformedBriefs()
    {
        await using var db = Database();
        var business = Guid.NewGuid();
        var repository = new SourceControlRepository { Id = Guid.NewGuid(), OrganizationId = business, Name = "empty" };
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = business };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = business, WorkstreamId = project.Id };
        db.AddRange(repository, project, board);
        foreach (var brief in new[] { "{broken", "null", "[]", "{\"repositoryId\":123}" })
            db.CoreWorkTasks.Add(new() { Id = Guid.NewGuid(), OrganizationId = business, BoardId = board.Id, DevelopmentBriefJson = brief });
        await db.SaveChangesAsync();
        var result = Assert.Single(await RepositoryDirectoryProjection.PopulateAsync(db, business, [Summary(repository)], default));
        Assert.Empty(result.Projects!); Assert.Empty(result.Access!); Assert.Null(result.LastEventOccurredAt);
    }

    private static OrganizationUser Agent(CSweetDbContext db, Guid business, string name, bool enabled = true)
    {
        var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = business.ToString("D"), IsEnabled = enabled };
        var employee = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = business, DisplayName = name,
            EmployeeType = EmployeeType.Agent, AgentInstallationId = installation.Id };
        db.AddRange(installation, employee);
        return employee;
    }
    private static OrganizationUser Human(Guid business, string name, OrganizationPermissionLevel permission) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = business, ApplicationUserId = Guid.NewGuid(), DisplayName = name,
        EmployeeType = EmployeeType.Human, PermissionLevel = permission
    };
    private static SourceControlRepositorySummary Summary(SourceControlRepository r) => new(r.Id, r.ConnectionId, r.Name, "", "main", "Ready", true, false, null, null, r.CreatedAt);
    private static CSweetDbContext Database() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
