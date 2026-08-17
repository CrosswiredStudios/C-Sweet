using System.Text.Json;
using CSweet.AgentHost.Broker;
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
using SharedWork = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkManagementCapabilityHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AgentSdkWorkCapabilitiesMatchPlatformWireActions()
    {
        var platformAgentActions = WorkBoardActions.All
            .Concat(WorkItemActions.All)
            .Concat(WorkSprintActions.All)
            .Concat(WorkOrchestrationActions.All)
            .Concat(PersonalTodoActions.All)
            .Append(SharedWork.WorkManagementCapabilityNames.ExecutionRunV1)
            .Where(SharedWork.WorkManagementCapabilityNames.All.Contains)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            SharedWork.WorkManagementCapabilityNames.All.Order(StringComparer.Ordinal),
            platformAgentActions.Order(StringComparer.Ordinal));
        Assert.Equal(WorkBoardActions.Read, CSweet.Agent.SDK.WorkBoardCapabilities.Read);
        Assert.Equal(WorkBoardActions.Create, CSweet.Agent.SDK.WorkBoardCapabilities.Create);
        Assert.Equal(WorkItemActions.Read, CSweet.Agent.SDK.WorkItemCapabilities.Read);
        Assert.Equal(WorkItemActions.Create, CSweet.Agent.SDK.WorkItemCapabilities.Create);
        Assert.Equal(WorkItemActions.Comment, CSweet.Agent.SDK.WorkItemCapabilities.Comment);
        Assert.Equal(WorkItemActions.Estimate, CSweet.Agent.SDK.WorkItemCapabilities.Estimate);
        Assert.Equal(WorkItemActions.Transfer, CSweet.Agent.SDK.WorkItemCapabilities.Transfer);
        Assert.Equal(WorkSprintActions.Read, CSweet.Agent.SDK.WorkSprintCapabilities.Read);
        Assert.Equal(WorkSprintActions.Create, CSweet.Agent.SDK.WorkSprintCapabilities.Create);
        Assert.Equal(WorkSprintActions.ManageScope, CSweet.Agent.SDK.WorkSprintCapabilities.ManageScope);
        Assert.Equal(WorkSprintActions.ManageCapacity, CSweet.Agent.SDK.WorkSprintCapabilities.ManageCapacity);
        Assert.Equal(WorkSprintActions.CarryOver, CSweet.Agent.SDK.WorkSprintCapabilities.CarryOver);
        Assert.Equal(WorkSprintActions.ReadReports, CSweet.Agent.SDK.WorkSprintCapabilities.ReadReports);
        Assert.Equal(WorkOrchestrationActions.Start, CSweet.Agent.SDK.WorkOrchestrationCapabilities.Start);
        Assert.Equal(SharedWork.WorkManagementCapabilityNames.ExecutionRunV1,
            CSweet.Agent.SDK.WorkOrchestrationCapabilities.Execute);
    }

    [Fact]
    public async Task PackageCapabilityWithoutScopedGrantIsDenied()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());

        var result = await InvokeAsync(
            handler,
            Session(setup, WorkBoardActions.Create),
            WorkBoardActions.Create,
            new { name = "Engineering", idempotencyKey = "create-board-1" });

        Assert.False(result.Succeeded);
        Assert.Contains("does not have", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.WorkBoards);
    }

    [Fact]
    public async Task CreateBoardIsScopedAndIdempotent()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        Grant(db, setup, WorkBoardActions.Create, GrantScopeKind.Organization, null);
        await db.SaveChangesAsync();
        var audit = new TestAuditEventWriter();
        var handler = CreateHandler(db, audit);
        var session = Session(setup, WorkBoardActions.Create);

        var first = await InvokeAsync(
            handler, session, WorkBoardActions.Create,
            new { name = "Engineering", description = "Delivery", idempotencyKey = "create-board-1" });
        var replay = await InvokeAsync(
            handler, session, WorkBoardActions.Create,
            new { name = "Ignored on replay", idempotencyKey = "create-board-1" });

        Assert.True(first.Succeeded, first.Error);
        Assert.True(replay.Succeeded, replay.Error);
        using var firstJson = JsonDocument.Parse(first.Payload.ToByteArray());
        using var replayJson = JsonDocument.Parse(replay.Payload.ToByteArray());
        Assert.Equal(
            firstJson.RootElement.GetProperty("id").GetGuid(),
            replayJson.RootElement.GetProperty("id").GetGuid());
        Assert.Single(db.WorkBoards);
        Assert.Single(db.WorkItemMutationReceipts);
        Assert.Contains(audit.Events, x => x.EventType == WorkBoardActions.Create);
    }

    [Fact]
    public async Task PlanningTicket_IsFinalizedInPlaceIdempotentlyAfterRepositoryApproval()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var accountableUserId = db.CoreOrganizationUsers.Local.Single().Id;
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            Name = "Delivery", Description = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Columns = [Column("Backlog", WorkBoardColumnCategory.ToDo, 0)]
        };
        var policy = new WorkOrchestrationPolicy
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            Name = "Software delivery", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var revision = new WorkOrchestrationPolicyRevision
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId, BoardId = board.Id,
            PolicyId = policy.Id, Revision = 1, Name = policy.Name, InitialStageKey = "development",
            IsPublished = true, CreatedAt = DateTimeOffset.UtcNow, PublishedAt = DateTimeOffset.UtcNow
        };
        revision.Stages.Add(new WorkOrchestrationStage
        {
            Id = Guid.NewGuid(), PolicyRevisionId = revision.Id, Key = "development",
            Name = "Development", Type = WorkOrchestrationStageType.AgentExecution
        });
        policy.PublishedRevisionId = revision.Id;
        policy.Revisions.Add(revision);
        board.OrchestrationPolicies.Add(policy);
        db.WorkBoards.Add(board);
        Grant(db, setup, WorkItemActions.Create, GrantScopeKind.Board, board.Id);
        Grant(db, setup, WorkItemActions.FinalizeDelivery, GrantScopeKind.Board, board.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(setup, WorkItemActions.Create, WorkItemActions.FinalizeDelivery);

        var created = await InvokeAsync(handler, session, WorkItemActions.Create, new
        {
            boardId = board.Id,
            title = "Implement flight controls",
            kind = "Story",
            priority = "High",
            idempotencyKey = "planning-ticket-1",
            planning = new
            {
                requirements = new[] { "Support pitch and yaw input." },
                acceptanceCriteria = new[] { "Controls respond within one rendered frame." },
                constraints = new[] { "Keep input handling deterministic." }
            }
        });
        Assert.True(created.Succeeded, created.Error);
        using var createdJson = JsonDocument.Parse(created.Payload.ToByteArray());
        var itemId = createdJson.RootElement.GetProperty("id").GetGuid();
        var itemRevision = createdJson.RootElement.GetProperty("revision").GetInt64();
        Assert.Equal(JsonValueKind.Object, createdJson.RootElement.GetProperty("planning").ValueKind);
        Assert.Equal(JsonValueKind.Null, createdJson.RootElement.GetProperty("delivery").ValueKind);

        var finalizePayload = new
        {
            boardId = board.Id,
            itemId,
            delivery = new
            {
                repositoryId = Guid.NewGuid(),
                baseBranch = "main",
                requirements = new[] { "Support pitch and yaw input." },
                acceptanceCriteria = new[] { "Controls respond within one rendered frame." },
                constraints = new[] { "Keep input handling deterministic." }
            },
            accountableOrganizationUserId = accountableUserId,
            stageAssignments = new[]
            {
                new
                {
                    stageKey = "development",
                    principalKind = "AgentInstallation",
                    agentInstallationId = setup.InstallationId
                }
            },
            expectedRevision = itemRevision,
            idempotencyKey = "finalize-ticket-1"
        };
        var finalized = await InvokeAsync(
            handler, session, WorkItemActions.FinalizeDelivery, finalizePayload);
        var replay = await InvokeAsync(
            handler, session, WorkItemActions.FinalizeDelivery, finalizePayload);

        Assert.True(finalized.Succeeded, finalized.Error);
        Assert.True(replay.Succeeded, replay.Error);
        using var finalizedJson = JsonDocument.Parse(finalized.Payload.ToByteArray());
        using var replayJson = JsonDocument.Parse(replay.Payload.ToByteArray());
        Assert.Equal(itemId, finalizedJson.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(itemId, replayJson.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("main", finalizedJson.RootElement.GetProperty("delivery").GetProperty("baseBranch").GetString());
        Assert.Single(await db.CoreWorkTasks.ToListAsync());
        Assert.Single(await db.WorkItemStageAssignments.ToListAsync());
    }

    [Fact(Skip = "Replaced by durable orchestration execution tests.")]
    public async Task AgentCanCreateReadAndCompleteStoryWithSeparateGrants()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            Name = "Delivery",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns =
            [
                Column("Ready", WorkBoardColumnCategory.ToDo, 0),
                Column("Done", WorkBoardColumnCategory.Done, 1)
            ]
        };
        db.WorkBoards.Add(board);
        foreach (var action in new[]
                 {
                     WorkBoardActions.Read,
                     WorkItemActions.Read,
                     WorkItemActions.Create,
                     WorkItemActions.Complete
                 })
            Grant(db, setup, action, GrantScopeKind.Board, board.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(
            setup,
            WorkBoardActions.Read,
            WorkItemActions.Read,
            WorkItemActions.Create,
            WorkItemActions.Complete);

        var created = await InvokeAsync(
            handler,
            session,
            WorkItemActions.Create,
            new
            {
                boardId = board.Id,
                title = "Ship secure Kanban",
                kind = "Story",
                priority = "High",
                idempotencyKey = "story-1"
            });
        Assert.True(created.Succeeded, created.Error);
        using var createdJson = JsonDocument.Parse(created.Payload.ToByteArray());
        var itemId = createdJson.RootElement.GetProperty("id").GetGuid();
        var revision = createdJson.RootElement.GetProperty("revision").GetInt64();

        var completed = await InvokeAsync(
            handler,
            session,
            WorkItemActions.Complete,
            new
            {
                boardId = board.Id,
                itemId,
                expectedRevision = revision,
                idempotencyKey = "complete-story-1"
            });
        Assert.True(completed.Succeeded, completed.Error);
        using var completedJson = JsonDocument.Parse(completed.Payload.ToByteArray());
        Assert.Equal("Completed", completedJson.RootElement.GetProperty("status").GetString());

        var read = await InvokeAsync(
            handler,
            session,
            WorkItemActions.Read,
            new { boardId = board.Id });
        Assert.True(read.Succeeded, read.Error);
        using var readJson = JsonDocument.Parse(read.Payload.ToByteArray());
        Assert.Equal(2, readJson.RootElement.GetProperty("columns").GetArrayLength());
        Assert.Single(readJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task AgentCanCommentIdempotentlyAndTransferWithBothBoardGrants()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var source = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            Name = "Intake",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns = [Column("To Do", WorkBoardColumnCategory.ToDo, 0)]
        };
        var target = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            Name = "Delivery",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns = [Column("Ready", WorkBoardColumnCategory.ToDo, 0)]
        };
        var item = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            BoardId = source.Id,
            BoardColumnId = source.Columns.Single().Id,
            Kind = WorkItemKind.Story,
            Title = "Transfer me",
            Description = "",
            Status = WorkTaskStatus.Ready,
            Priority = WorkTaskPriority.Medium,
            BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.WorkBoards.AddRange(source, target);
        db.CoreWorkTasks.Add(item);
        Grant(db, setup, WorkItemActions.Comment, GrantScopeKind.Board, source.Id);
        Grant(db, setup, WorkItemActions.Transfer, GrantScopeKind.Board, source.Id);
        Grant(db, setup, WorkItemActions.Transfer, GrantScopeKind.Board, target.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(
            setup, WorkItemActions.Comment, WorkItemActions.Transfer);

        var comment = await InvokeAsync(
            handler, session, WorkItemActions.Comment,
            new
            {
                boardId = source.Id,
                itemId = item.Id,
                body = "Ready for delivery.",
                idempotencyKey = "comment-1"
            });
        var commentReplay = await InvokeAsync(
            handler, session, WorkItemActions.Comment,
            new
            {
                boardId = source.Id,
                itemId = item.Id,
                body = "Ignored on replay.",
                idempotencyKey = "comment-1"
            });
        var transfer = await InvokeAsync(
            handler, session, WorkItemActions.Transfer,
            new
            {
                boardId = source.Id,
                itemId = item.Id,
                targetBoardId = target.Id,
                expectedRevision = item.Revision,
                idempotencyKey = "transfer-1"
            });

        Assert.True(comment.Succeeded, comment.Error);
        Assert.True(commentReplay.Succeeded, commentReplay.Error);
        Assert.True(transfer.Succeeded, transfer.Error);
        using var transferJson = JsonDocument.Parse(transfer.Payload.ToByteArray());
        Assert.Equal(
            target.Id,
            transferJson.RootElement.GetProperty("targetBoardId").GetGuid());
        Assert.Equal(target.Id, (await db.CoreWorkTasks.SingleAsync()).BoardId);
        Assert.Single(db.WorkItemComments);
        Assert.Equal(2, db.WorkItemActivities.Count());
        Assert.Equal(2, db.WorkItemMutationReceipts.Count());
        Assert.Equal(3, db.ApplicationRealtimeOutbox.Count());
    }

    [Fact(Skip = "Direct sprint state transitions were removed in favor of manager orchestration.")]
    public async Task AgentCanPlanCompleteAndReportSprintWithSeparateGrants()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            Name = "Delivery",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns = [Column("To Do", WorkBoardColumnCategory.ToDo, 0)]
        };
        var item = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            BoardId = board.Id,
            BoardColumnId = board.Columns.Single().Id,
            Kind = WorkItemKind.Story,
            Title = "Sprint story",
            Description = "",
            Status = WorkTaskStatus.Ready,
            Priority = WorkTaskPriority.Medium,
            BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.WorkBoards.Add(board);
        db.CoreWorkTasks.Add(item);
        foreach (var action in new[]
                 {
                     WorkBoardActions.Read,
                     WorkSprintActions.Read,
                     WorkSprintActions.Create,
                     WorkSprintActions.Start,
                     WorkSprintActions.Complete,
                     WorkSprintActions.ManageScope,
                     WorkSprintActions.ManageCapacity,
                     WorkSprintActions.ReadReports,
                     WorkItemActions.Estimate
                 })
            Grant(db, setup, action, GrantScopeKind.Board, board.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(
            setup,
            WorkSprintActions.Read,
            WorkSprintActions.Create,
            WorkSprintActions.Start,
            WorkSprintActions.Complete,
            WorkSprintActions.ManageScope,
            WorkSprintActions.ManageCapacity,
            WorkSprintActions.ReadReports,
            WorkItemActions.Estimate);

        var created = await InvokeAsync(
            handler, session, WorkSprintActions.Create,
            new
            {
                boardId = board.Id,
                name = "Sprint 1",
                goal = "Ship the story",
                idempotencyKey = "create-sprint-1"
            });
        Assert.True(created.Succeeded, created.Error);
        using var createdJson = JsonDocument.Parse(created.Payload.ToByteArray());
        var sprintId = createdJson.RootElement.GetProperty("id").GetGuid();
        var sprintRevision = createdJson.RootElement.GetProperty("revision").GetInt64();

        var estimated = await InvokeAsync(
            handler, session, WorkItemActions.Estimate,
            new
            {
                boardId = board.Id,
                itemId = item.Id,
                estimatePoints = 5,
                expectedItemRevision = item.Revision,
                idempotencyKey = "estimate-1"
            });
        Assert.True(estimated.Succeeded, estimated.Error);
        using var estimatedJson = JsonDocument.Parse(estimated.Payload.ToByteArray());
        var itemRevision = estimatedJson.RootElement.GetProperty("revision").GetInt64();
        var capacity = await InvokeAsync(
            handler, session, WorkSprintActions.ManageCapacity,
            new
            {
                boardId = board.Id,
                sprintId,
                capacityPoints = 8,
                expectedSprintRevision = sprintRevision,
                idempotencyKey = "capacity-1"
            });
        Assert.True(capacity.Succeeded, capacity.Error);
        using var capacityJson = JsonDocument.Parse(capacity.Payload.ToByteArray());
        sprintRevision = capacityJson.RootElement.GetProperty("revision").GetInt64();
        var scoped = await InvokeAsync(
            handler, session, WorkSprintActions.ManageScope,
            new
            {
                boardId = board.Id,
                itemId = item.Id,
                sprintId,
                expectedItemRevision = itemRevision,
                idempotencyKey = "scope-1"
            });
        var started = await InvokeAsync(
            handler, session, WorkSprintActions.Start,
            new
            {
                boardId = board.Id,
                sprintId,
                expectedRevision = sprintRevision,
                idempotencyKey = "start-1"
            });
        Assert.True(started.Succeeded, started.Error);
        using var startedJson = JsonDocument.Parse(started.Payload.ToByteArray());
        var startedRevision = startedJson.RootElement.GetProperty("revision").GetInt64();
        var completed = await InvokeAsync(
            handler, session, WorkSprintActions.Complete,
            new
            {
                boardId = board.Id,
                sprintId,
                expectedRevision = startedRevision,
                idempotencyKey = "complete-1"
            });
        var listed = await InvokeAsync(
            handler, session, WorkSprintActions.Read,
            new { boardId = board.Id });
        var report = await InvokeAsync(
            handler, session, WorkSprintActions.ReadReports,
            new { boardId = board.Id });

        Assert.True(scoped.Succeeded, scoped.Error);
        Assert.True(completed.Succeeded, completed.Error);
        Assert.True(listed.Succeeded, listed.Error);
        Assert.True(report.Succeeded, report.Error);
        using var listedJson = JsonDocument.Parse(listed.Payload.ToByteArray());
        var sprint = Assert.Single(listedJson.RootElement.EnumerateArray());
        Assert.Equal("Completed", sprint.GetProperty("status").GetString());
        Assert.Equal(1, sprint.GetProperty("itemCount").GetInt32());
        Assert.Equal(5, sprint.GetProperty("plannedPoints").GetDecimal());
        Assert.Equal(sprintId, (await db.CoreWorkTasks.SingleAsync()).SprintId);
        Assert.Equal(6, db.WorkSprintMutationReceipts.Count());
        Assert.Single(db.WorkSprintSnapshots);
    }

    [Fact(Skip = "Generic move automation was removed in favor of policy transitions.")]
    public async Task AgentCanManageAutomationButCannotSelfGrantItsExecutionIdentity()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            Name = "Delivery",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns =
            [
                Column("To Do", WorkBoardColumnCategory.ToDo, 0),
                Column("Doing", WorkBoardColumnCategory.InProgress, 1)
            ]
        };
        db.WorkBoards.Add(board);
        foreach (var action in new[]
                 {
                     WorkBoardActions.Read,
                     WorkAutomationActions.Read,
                     WorkAutomationActions.Manage
                 })
            Grant(db, setup, action, GrantScopeKind.Board, board.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(
            setup, WorkBoardActions.Read,
            WorkAutomationActions.Read, WorkAutomationActions.Manage);
        var targetId = board.Columns.Last().Id;

        var created = await InvokeAsync(
            handler, session, WorkAutomationActions.Manage,
            new
            {
                boardId = board.Id,
                operation = "Create",
                name = "Start new work",
                triggerEventType = "item.created",
                action = WorkItemActions.Move,
                targetColumnId = targetId,
                isEnabled = false,
                idempotencyKey = "automation-create-1"
            });
        var replay = await InvokeAsync(
            handler, session, WorkAutomationActions.Manage,
            new
            {
                boardId = board.Id,
                operation = "Create",
                idempotencyKey = "automation-create-1"
            });
        var listed = await InvokeAsync(
            handler, session, WorkAutomationActions.Read,
            new { boardId = board.Id });

        Assert.True(created.Succeeded, created.Error);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.True(listed.Succeeded, listed.Error);
        using var createdJson = JsonDocument.Parse(created.Payload.ToByteArray());
        using var replayJson = JsonDocument.Parse(replay.Payload.ToByteArray());
        var ruleId = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(ruleId, createdJson.RootElement.GetProperty("automationIdentityId").GetGuid());
        Assert.Equal(ruleId, replayJson.RootElement.GetProperty("id").GetGuid());
        Assert.False(createdJson.RootElement.GetProperty("hasExecutionGrant").GetBoolean());
        Assert.DoesNotContain(db.ScopedActionGrants, x =>
            x.SubjectKind == GrantSubjectKind.AutomationIdentity);
        Assert.Single(db.WorkSprintMutationReceipts.Where(
            x => x.Action == WorkAutomationActions.Manage));
    }

    [Fact]
    public async Task TeamScopedAgentCanConfigureSoftwareColumnsAndMoveOnlyItsTeamItem()
    {
        await using var db = CreateDb();
        var setup = SeedInstallation(db);
        var leadId = db.ChangeTracker.Entries<OrganizationUser>().Single().Entity.Id;
        var team = new OrganizationTeam
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            TeamKey = "software-team",
            NormalizedName = "SOFTWARE TEAM",
            Name = "Software Team",
            LeadOrganizationUserId = leadId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var board = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            TeamId = team.Id,
            ManagerOrganizationUserId = leadId,
            Name = "Software",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns =
            [
                Column("To Do", WorkBoardColumnCategory.ToDo, 0),
                Column("Done", WorkBoardColumnCategory.Done, 1)
            ]
        };
        var otherTeam = new OrganizationTeam
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            TeamKey = "other-team",
            NormalizedName = "OTHER TEAM",
            Name = "Other Team",
            LeadOrganizationUserId = leadId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var otherBoard = new WorkBoard
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            TeamId = otherTeam.Id,
            ManagerOrganizationUserId = leadId,
            Name = "Other Software",
            Description = "",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Columns =
            [
                Column("Backlog", WorkBoardColumnCategory.ToDo, 0),
                Column("Ready For Development", WorkBoardColumnCategory.ToDo, 1),
                Column("Done", WorkBoardColumnCategory.Done, 2)
            ]
        };
        db.OrganizationTeams.AddRange(team, otherTeam);
        db.WorkBoards.AddRange(board, otherBoard);
        foreach (var action in new[]
                 {
                     WorkBoardActions.Read,
                     WorkBoardActions.ConfigureColumns,
                     WorkItemActions.Read,
                     WorkItemActions.Move
                 })
            Grant(db, setup, action, GrantScopeKind.Team, team.Id);
        await db.SaveChangesAsync();
        var handler = CreateHandler(db, new TestAuditEventWriter());
        var session = Session(
            setup, WorkBoardActions.Read, WorkBoardActions.ConfigureColumns,
            WorkItemActions.Read, WorkItemActions.Move);

        var softwareColumns = new[]
        {
            new { name = "Backlog", category = "ToDo", wipPolicy = "Disabled" },
            new { name = "Ready For Development", category = "ToDo", wipPolicy = "Disabled" },
            new { name = "In Development", category = "InProgress", wipPolicy = "Disabled" },
            new { name = "Dev Complete", category = "InProgress", wipPolicy = "Disabled" },
            new { name = "In Testing", category = "InProgress", wipPolicy = "Disabled" },
            new { name = "Ready To Merge", category = "InProgress", wipPolicy = "Disabled" },
            new { name = "Done", category = "Done", wipPolicy = "Disabled" }
        };
        object ConfigurePayload(Guid boardId, long revision, string key) => new
        {
            boardId,
            expectedRevision = revision,
            columns = softwareColumns,
            idempotencyKey = key
        };
        var initialRevision = board.Revision;
        var configured = await InvokeAsync(
            handler, session, WorkBoardActions.ConfigureColumns,
            ConfigurePayload(board.Id, initialRevision, "software-columns-v1"));
        var replay = await InvokeAsync(
            handler, session, WorkBoardActions.ConfigureColumns,
            ConfigurePayload(board.Id, initialRevision, "software-columns-v1"));
        var conflict = await InvokeAsync(
            handler, session, WorkBoardActions.ConfigureColumns,
            ConfigurePayload(board.Id, initialRevision, "software-columns-conflict"));
        var crossTeamConfigure = await InvokeAsync(
            handler, session, WorkBoardActions.ConfigureColumns,
            ConfigurePayload(otherBoard.Id, otherBoard.Revision, "other-software-columns"));
        Assert.True(configured.Succeeded, configured.Error);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.False(conflict.Succeeded);
        Assert.False(crossTeamConfigure.Succeeded);
        using (var configuredJson = JsonDocument.Parse(configured.Payload.ToByteArray()))
        {
            var columns = configuredJson.RootElement.GetProperty("columns").EnumerateArray().ToList();
            Assert.Equal(
                softwareColumns.Select(x => x.name),
                columns.Select(x => x.GetProperty("name").GetString()));
            Assert.All(columns, column =>
            {
                Assert.Equal("Disabled", column.GetProperty("wipPolicy").GetString());
                Assert.Equal(JsonValueKind.Null, column.GetProperty("wipLimit").ValueKind);
            });
        }
        var backlog = await db.WorkBoardColumns.SingleAsync(x => x.BoardId == board.Id && x.Name == "Backlog");
        var ready = await db.WorkBoardColumns.SingleAsync(x => x.BoardId == board.Id && x.Name == "Ready For Development");
        var item = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            BoardId = board.Id,
            BoardColumnId = backlog.Id,
            Kind = WorkItemKind.Story,
            Title = "First sprint story",
            Description = "",
            Status = WorkTaskStatus.Ready,
            Priority = WorkTaskPriority.High,
            BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreWorkTasks.Add(item);
        var otherItem = new WorkTask
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            BoardId = otherBoard.Id,
            BoardColumnId = otherBoard.Columns.Single(x => x.Name == "Backlog").Id,
            Kind = WorkItemKind.Story,
            Title = "Other team story",
            Description = "",
            Status = WorkTaskStatus.Ready,
            Priority = WorkTaskPriority.High,
            BoardRank = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.CoreWorkTasks.Add(otherItem);
        await db.SaveChangesAsync();

        var moved = await InvokeAsync(
            handler, session, WorkItemActions.Move,
            new
            {
                boardId = board.Id,
                itemId = item.Id,
                targetColumnId = ready.Id,
                expectedRevision = item.Revision,
                idempotencyKey = "first-sprint-ready"
            });
        var listed = await InvokeAsync(
            handler, session, WorkBoardActions.Read,
            new { includeArchived = false });
        var crossTeamMove = await InvokeAsync(
            handler, session, WorkItemActions.Move,
            new
            {
                boardId = otherBoard.Id,
                itemId = otherItem.Id,
                targetColumnId = otherBoard.Columns.Single(x => x.Name == "Ready For Development").Id,
                expectedRevision = otherItem.Revision,
                idempotencyKey = "other-team-ready"
            });

        Assert.True(moved.Succeeded, moved.Error);
        Assert.True(listed.Succeeded, listed.Error);
        Assert.False(crossTeamMove.Succeeded);
        Assert.Equal(ready.Id, (await db.CoreWorkTasks.SingleAsync(x => x.Id == item.Id)).BoardColumnId);
        using var boardsJson = JsonDocument.Parse(listed.Payload.ToByteArray());
        Assert.Equal(board.Id, Assert.Single(boardsJson.RootElement.EnumerateArray()).GetProperty("id").GetGuid());
    }

    private static WorkManagementCapabilityHandler CreateHandler(
        CSweetDbContext db,
        TestAuditEventWriter audit)
    {
        IScopedActionAuthorizationService authorization =
            new ScopedActionAuthorizationService(db);
        return new WorkManagementCapabilityHandler(
            db, authorization, audit,
            new WorkOrchestrationService(db, TimeProvider.System));
    }

    private static async Task<CapabilityResult> InvokeAsync(
        WorkManagementCapabilityHandler handler,
        AgentSession session,
        string capability,
        object payload)
    {
        var request = new RequestCapability
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Capability = capability,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions))
        };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, CancellationToken.None))
            results.Add(result);
        return Assert.Single(results);
    }

    private static AgentSession Session(
        Setup setup,
        params string[] capabilities) => new(
        Guid.NewGuid().ToString("N"),
        "com.example.delivery-agent",
        setup.InstallationId.ToString("D"),
        setup.OrganizationId.ToString("D"),
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        new AuthorizedAgentGrant(
            new HashSet<string>(),
            new HashSet<string>(),
            capabilities.ToHashSet(StringComparer.Ordinal),
            1));

    private static Setup SeedInstallation(CSweetDbContext db)
    {
        var setup = new Setup(Guid.NewGuid(), Guid.NewGuid());
        db.CoreOrganizations.Add(new Organization
        {
            Id = setup.OrganizationId,
            Name = "Test company",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = setup.InstallationId,
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = Guid.NewGuid(),
            BusinessId = setup.OrganizationId.ToString("D"),
            RevisionStatus = PluginRevisionStatus.Active,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = setup.OrganizationId,
            AgentInstallationId = setup.InstallationId, DisplayName = "Test agent",
            EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Manager,
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        });
        return setup;
    }

    private static void Grant(
        CSweetDbContext db,
        Setup setup,
        string action,
        GrantScopeKind scopeKind,
        Guid? scopeId) =>
        db.ScopedActionGrants.Add(new ScopedActionGrant
        {
            Id = Guid.NewGuid(),
            OrganizationId = setup.OrganizationId,
            SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = setup.InstallationId,
            Action = action,
            ScopeKind = scopeKind,
            ScopeId = scopeId,
            GrantedBySubjectKind = GrantSubjectKind.OrganizationUser,
            GrantedAt = DateTimeOffset.UtcNow
        });

    private static WorkBoardColumn Column(
        string name,
        WorkBoardColumnCategory category,
        int position) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = category,
            Position = position
        };

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed record Setup(Guid OrganizationId, Guid InstallationId);
}
