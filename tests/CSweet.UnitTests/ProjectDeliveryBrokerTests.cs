using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkManagementCapabilityHandlerTests
{
    [Fact]
    public async Task ApprovedManagerSetupGrantsCanConfigureItsRealDeliveryProfileThroughTheBroker()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var setup = SeedInstallation(db);
        var manager = db.CoreOrganizationUsers.Local.Single();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "CSweet.Agent.Producer.VideoGame"))) root = root.Parent;
        Assert.NotNull(root);
        var json = File.ReadAllText(Path.Combine(root.FullName, "CSweet.Agent.Producer.VideoGame/profiles/video-game-manager-brief.v2.json"));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, Name = "Game",
            Status = WorkstreamStatus.Approved, ProfileKey = "video-game-manager-brief.v1", ProfileVersion = 2,
            ProfileDefinitionDigest = digest, AccountableManagerOrganizationUserId = manager.Id };
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, LeadOrganizationUserId = manager.Id, Name = "Delivery", TeamKey = "delivery" };
        var staffing = new ResourceChangeRequestRecord { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            RequesterInstallationId = setup.InstallationId, RequesterOrganizationUserId = manager.Id,
            Status = ResourceChangeRequestStatus.Approved, TeamId = team.Id };
        db.AddRange(project, team, staffing,
            new TeamMembership { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, TeamId = team.Id, OrganizationUserId = manager.Id },
            new WorkstreamAuthorityEnvelopeRecord { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, WorkstreamId = project.Id, AgentAuthorizedActionKeysJson = "[\"routine-staffing\"]" },
            new WorkstreamProfileDefinitionRecord { Id = Guid.NewGuid(), Key = project.ProfileKey, Version = 2,
                DefinitionDigest = digest, DefinitionJson = json, MetadataSchemaJson = "{}", Status = "Active" });
        await db.SaveChangesAsync();
        var prepared = await new ProjectSetupService(db, TimeProvider.System, new(db, TimeProvider.System)).PrepareDeliveryAsync(
            setup.OrganizationId, setup.InstallationId, new(project.Id, staffing.Id, [manager.Id], project.Revision, "approved-delivery"), default);
        db.ChangeTracker.Clear();
        var board = await db.WorkBoards.AsNoTracking().SingleAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var request = new Wire.ConfigureProfileOrchestrationRequest(project.Id, prepared.BoardId, board.Revision, digest, "manager-profile");
        var result = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.ConfigureProfile), WorkOrchestrationActions.ConfigureProfile, request);
        Assert.True(result.Succeeded, result.Error);
        db.ChangeTracker.Clear();
        var revision = await db.WorkOrchestrationPolicyRevisions.Include(x => x.Stages).SingleAsync();
        Assert.True(revision.IsPublished);
        Assert.Contains(revision.Stages, x => x.Key == "quality");
        Assert.Contains(revision.Stages, x => x.Key == "merge-decision");
        Assert.Contains(revision.Stages, x => x.PlatformAction == "source-control.merge.execute.v2");
        var replay = await InvokeAsync(handler, Session(setup, WorkOrchestrationActions.ConfigureProfile), WorkOrchestrationActions.ConfigureProfile,
            request with { ExpectedBoardRevision = (await db.WorkBoards.SingleAsync()).Revision });
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Single(await db.WorkOrchestrationPolicyRevisions.ToListAsync());
        var metrics = await InvokeAsync(handler, Session(setup, WorkFlowMetricActions.Read), WorkFlowMetricActions.Read, new Wire.ReadWorkFlowMetricsRequest(board.Id));
        Assert.True(metrics.Succeeded, metrics.Error);
        var columns = await db.WorkBoardColumns.AsNoTracking().Where(x => x.BoardId == board.Id).ToListAsync();
        var container = new WorkTask { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            BoardColumnId = columns.Single(x => x.Name == "Ready").Id, Title = "Delivered outcome", IsExecutable = false,
            TypeKey = Wire.WorkItemTypeKeys.GeneralEpicV1, Revision = 1 };
        db.CoreWorkTasks.Add(container); await db.SaveChangesAsync();
        var unfinished = new WorkTask { Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            BoardColumnId = container.BoardColumnId, ParentWorkTaskId = container.Id, Title = "Still executing", IsExecutable = true, Status = WorkTaskStatus.Running };
        db.CoreWorkTasks.Add(unfinished); await db.SaveChangesAsync();
        var premature = await InvokeAsync(handler, Session(setup, WorkItemActions.Move), WorkItemActions.Move,
            new Wire.MoveWorkItemRequest(board.Id, container.Id, columns.Single(x => x.Name == "Done").Id, container.Revision, "close-container"));
        Assert.False(premature.Succeeded); Assert.Contains("children", premature.Error);
        var directExecutionClose = await InvokeAsync(handler, Session(setup, WorkItemActions.Move), WorkItemActions.Move,
            new Wire.MoveWorkItemRequest(board.Id, unfinished.Id, columns.Single(x => x.Name == "Done").Id, unfinished.Revision, "cannot-skip-execution"));
        Assert.False(directExecutionClose.Succeeded);
        var currentBoard = await db.WorkBoards.SingleAsync();
        currentBoard.ManagerOrganizationUserId = Guid.NewGuid(); await db.SaveChangesAsync();
        var wrongManager = await InvokeAsync(handler, Session(setup, WorkItemActions.Move), WorkItemActions.Move,
            new Wire.MoveWorkItemRequest(board.Id, container.Id, columns.Single(x => x.Name == "Done").Id, container.Revision, "close-container"));
        Assert.False(wrongManager.Succeeded); Assert.Contains("current board manager", wrongManager.Error);
        currentBoard.ManagerOrganizationUserId = manager.Id; await db.SaveChangesAsync();        unfinished.Status = WorkTaskStatus.Completed; await db.SaveChangesAsync();        var completed = await InvokeAsync(handler, Session(setup, WorkItemActions.Move), WorkItemActions.Move,
            new Wire.MoveWorkItemRequest(board.Id, container.Id, columns.Single(x => x.Name == "Done").Id, container.Revision, "close-container"));
        Assert.True(completed.Succeeded, completed.Error);
        Assert.Equal(WorkTaskStatus.Completed, (await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == container.Id)).Status);
    }
}
