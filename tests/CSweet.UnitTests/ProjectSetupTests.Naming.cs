using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ProjectSetupTests
{
    [Fact]
    public async Task Shared_employee_has_no_implicit_project_until_only_one_remains_active()
    {
        await using var db = Db(); var (human, employee) = Seed(db); await db.SaveChangesAsync();
        var first = await Service(db).CreateAsync(human, Request(human, employee), default);
        var second = new Workstream { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId,
            Name = "Other game", Outcome = "Deliver", Status = WorkstreamStatus.Active };
        db.Workstreams.Add(second);
        db.ProjectParticipants.Add(new() { OrganizationId = human.OrganizationId, WorkstreamId = second.Id,
            OrganizationUserId = employee.Id, AddedByOrganizationUserId = human.Id, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var session = new CSweet.AgentHost.Broker.AgentSession("test", "agent", employee.AgentInstallationId!.Value.ToString("D"),
            human.OrganizationId.ToString("D"), "runtime", "tick", new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), 1));
        var resolver = new CSweet.AgentHost.Broker.AgentEmployeeIdentityResolver(db);
        Assert.Null(await resolver.ReadProjectAssignmentAsync(session));
        second.Status = WorkstreamStatus.Completed; await db.SaveChangesAsync();
        Assert.Equal(first.ProjectId, (await resolver.ReadProjectAssignmentAsync(session))!.WorkstreamId);
    }

    [Fact]
    public async Task Rename_preserves_execution_authority_and_identifiers_and_records_the_transition_once()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var service = Service(db); var created = await service.CreateAsync(human, Request(human, developer), default);
        var project = await db.Workstreams.SingleAsync(); var board = await db.WorkBoards.SingleAsync();
        var binding = await db.ProjectDeliveryBindings.SingleAsync(); binding.RepositoryId = Guid.NewGuid();
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, BoardId = board.Id,
            Title = "Prototype task", Status = WorkTaskStatus.Running, AssignedEmployeeId = developer.Id,
            AssignedAgentInstallationId = developer.AgentInstallationId, ClaimEventId = Guid.NewGuid(), ClaimExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        db.CoreWorkTasks.Add(item);
        db.WorkBoards.Add(new() { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, WorkstreamId = project.Id, Name = "Custom board" });
        await db.SaveChangesAsync();
        var boardKey = board.Key; var repository = binding.RepositoryId; var claim = item.ClaimEventId;
        var grants = await db.ScopedActionGrants.Select(x => new { x.Id, x.Revision, x.ScopeId, x.RevokedAt }).ToListAsync();
        var participants = await db.ProjectParticipants.Select(x => new { x.OrganizationUserId, x.Revision }).ToListAsync();
        var renamed = await service.RenameAsync(human, project.Id, new("  NEON BREAKER  ", created.Revision), default);
        Assert.Equal("NEON BREAKER", renamed.Name); Assert.Equal(2, renamed.Revision); Assert.Equal(created.ProjectId, renamed.ProjectId);
        Assert.Equal(renamed.Name, board.Name); Assert.Equal(boardKey, board.Key);
        Assert.Equal("Custom board", (await db.WorkBoards.SingleAsync(x => x.Id != board.Id)).Name);
        Assert.Equal(repository, binding.RepositoryId); Assert.Equal(created.BoardId, binding.BoardId);
        Assert.Equal(grants, await db.ScopedActionGrants.Select(x => new { x.Id, x.Revision, x.ScopeId, x.RevokedAt }).ToListAsync());
        Assert.Equal(participants, await db.ProjectParticipants.Select(x => new { x.OrganizationUserId, x.Revision }).ToListAsync());
        Assert.Equal(claim, item.ClaimEventId); Assert.Equal(WorkTaskStatus.Running, item.Status);
        await new ProjectWorkPolicy(db, TimeProvider.System).RequireWorkAsync(item, default);
        var session = new CSweet.AgentHost.Broker.AgentSession("test", "developer", developer.AgentInstallationId!.Value.ToString("D"),
            human.OrganizationId.ToString("D"), "runtime", "tick", new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), 1));
        var assignment = await new CSweet.AgentHost.Broker.AgentEmployeeIdentityResolver(db).ReadProjectAssignmentAsync(session);
        Assert.Equal(created.ProjectId, assignment!.WorkstreamId); Assert.Equal(renamed.Name, assignment.ProjectName);
        Assert.Equal(created.BoardId, assignment.BoardId); Assert.Equal(renamed.Revision, assignment.Revision);
        var audit = Assert.Single(await db.AuditOutbox.Where(x => x.SourceEntityId == project.Id).ToListAsync(),
            x => x.RequestJson.Contains("workstream.renamed.v1"));
        var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(audit.RequestJson)!;
        Assert.Equal(human.Id, request.Actor!.OrganizationUserId); Assert.Contains("Prototype", request.MetadataJson); Assert.Contains(renamed.Name, request.MetadataJson);
        var change = Assert.Single(await db.AgentPlatformEventOutbox.Where(x => x.EventType == CSweet.WorkManagement.Contracts.WorkstreamEventNames.ChangedV2).ToListAsync());
        Assert.Contains("Prototype", change.DataJson); Assert.Contains(renamed.Name, change.DataJson);
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.DataJson.Contains("\"Revision\":2"));
        var count = await db.AgentPlatformEventOutbox.CountAsync();
        Assert.Equal(renamed, await service.RenameAsync(human, project.Id, new(renamed.Name, 1), default));
        Assert.Equal(count, await db.AgentPlatformEventOutbox.CountAsync());
    }

    [Theory]
    [InlineData(160)]
    [InlineData(161)]
    [InlineData(512)]
    public async Task Long_project_names_respect_the_smaller_board_label_limit_and_can_be_renamed_again(int length)
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var service = Service(db); var created = await service.CreateAsync(human, Request(human, developer), default);
        var name = new string('n', length);
        await service.RenameAsync(human, created.ProjectId, new(name, 1), default);
        Assert.Equal(name, (await db.Workstreams.SingleAsync()).Name);
        Assert.Equal(name[..Math.Min(160, length)], (await db.WorkBoards.SingleAsync()).Name);
        await service.RenameAsync(human, created.ProjectId, new("Short name", 2), default);
        Assert.Equal("Short name", (await db.WorkBoards.SingleAsync()).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Empty_names_are_rejected(string? name)
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var created = await Service(db).CreateAsync(human, Request(human, developer), default);
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).RenameAsync(human, created.ProjectId, new(name!, 1), default));
        Assert.Equal("Prototype", (await db.Workstreams.SingleAsync()).Name);
    }

    [Fact]
    public async Task Rename_rejects_stale_edits_long_names_and_unauthorized_actors()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var service = Service(db); var created = await service.CreateAsync(human, Request(human, developer), default);
        await service.RenameAsync(human, created.ProjectId, new("New name", 1), default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.RenameAsync(human, created.ProjectId, new("Stale", 1), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync(human, created.ProjectId, new(new string('a', 513), 2), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameAsync(developer, created.ProjectId, new("Agent write", 2), default));
        human.PermissionLevel = OrganizationPermissionLevel.Contributor;
        (await db.Workstreams.SingleAsync()).AccountableManagerOrganizationUserId = developer.Id;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RenameAsync(human, created.ProjectId, new("Member write", 2), default));
        human.OrganizationId = Guid.NewGuid();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RenameAsync(human, created.ProjectId, new("Foreign write", 2), default));
        Assert.Equal("New name", (await db.Workstreams.SingleAsync()).Name);
    }

    [Fact]
    public async Task Legacy_project_without_delivery_binding_can_be_renamed_by_its_human_manager()
    {
        await using var db = Db(); var (human, _) = Seed(db);
        human.PermissionLevel = OrganizationPermissionLevel.Contributor;
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, Name = "Old", AccountableManagerOrganizationUserId = human.Id };
        db.Workstreams.Add(project); await db.SaveChangesAsync();
        await Service(db).RenameAsync(human, project.Id, new("New", 1), default);
        Assert.Equal("New", project.Name); Assert.Empty(db.ProjectDeliveryBindings);
    }

    [Fact]
    public async Task Approved_agent_name_change_uses_the_same_audit_board_and_realtime_effects()
    {
        await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
        var created = await Service(db).CreateAsync(human, Request(human, developer), default);
        var project = await db.Workstreams.SingleAsync();
        db.WorkstreamProfileDefinitions.Add(new() { Id = Guid.NewGuid(), Key = project.ProfileKey!, Version = project.ProfileVersion!.Value,
            DefinitionDigest = project.ProfileDefinitionDigest!, DefinitionJson = "{}", MetadataSchemaJson = "{}" });
        await db.SaveChangesAsync();
        var request = new CSweet.WorkManagement.Contracts.WorkstreamChangeProposalRequest(project.Id, project.Revision, "Rename",
            JsonSerializer.SerializeToElement(new { name = "Approved name" }), "User requested", "rename");
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, ActionType = "workstream.change.v1",
            PayloadJson = JsonSerializer.Serialize(new { profileDefinitionDigest = project.ProfileDefinitionDigest, payload = request }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        await new WorkstreamManagedActionExecutor(db, TimeProvider.System).ExecuteAsync(proposal, human); await db.SaveChangesAsync();
        Assert.Equal("Approved name", project.Name); Assert.Equal(project.Name, (await db.WorkBoards.SingleAsync()).Name);
        Assert.Contains(await db.AuditOutbox.ToListAsync(), x => x.RequestJson.Contains("workstream.renamed.v1"));
        Assert.Contains(await db.ApplicationRealtimeOutbox.ToListAsync(), x => x.DataJson.Contains("\"Revision\":2"));
    }

    [Fact]
    public async Task Repeated_renames_preserve_historical_memory_in_the_same_case_partition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-project-rename-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = Db(); var (human, developer) = Seed(db); await db.SaveChangesAsync();
            var service = Service(db); var created = await service.CreateAsync(human, Request(human, developer), default);
            var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = human.OrganizationId, BoardId = created.BoardId, Title = "Prototype requirement" };
            db.CoreWorkTasks.Add(item); await db.SaveChangesAsync();
            var ns = EmployeeMemoryNamespaces.Case(human.OrganizationId.ToString("D"), item.Id.ToString("D"), "csweet");
            await using var store = new SqliteMemoryStore(path);
            var now = DateTimeOffset.UtcNow;
            var episode = new MemoryEpisode(Guid.NewGuid(), ns.Partition, ns.Scope, "Prototype needs keyboard accessibility", "text/plain",
                new MemorySource("test", item.Id.ToString("D")), "checksum", now.AddMinutes(-1), now, "requirement", Sensitivity: MemorySensitivity.Internal);
            await store.AppendEpisodeAsync(episode);
            await service.RenameAsync(human, created.ProjectId, new("NEON BREAKER", 1), default);
            await service.RenameAsync(human, created.ProjectId, new("Final title", 2), default);
            var after = EmployeeMemoryNamespaces.Case(human.OrganizationId.ToString("D"), item.Id.ToString("D"), "csweet");
            Assert.Equal(ns, after);
            var recalled = await store.SearchAsync(new MemorySearchRequest(after.Partition, after.Scope, "keyboard accessibility", AsOf: DateTimeOffset.UtcNow));
            Assert.Contains(recalled, x => x.Id == episode.Id && x.Content == episode.Content);
            Assert.Equal(episode.Content, (await store.GetEpisodeAsync(ns.Partition, episode.Id))!.Content);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
