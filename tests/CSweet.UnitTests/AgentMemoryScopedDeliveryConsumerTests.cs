using System.Text.Json;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record ScopedDeliveryFixture(Guid Stage, Guid Attempt, Guid Execution, Guid Plan, Guid Project,
        Guid Board, string Scopes, string Candidate);

    private static async Task<ScopedDeliveryFixture> SeedScopedDeliveryConsumerAsync(DurabilityFixture fixture,
        ScopedAudienceFixture scoped, Guid workId, string scope = "Story", string key = "quality")
    {
        await using var db = fixture.Context();
        var item = await db.CoreWorkTasks.SingleAsync(x => x.Id == scoped.Item);
        var board = await db.WorkBoards.SingleAsync(x => x.Id == item.BoardId);
        var now = DateTimeOffset.UtcNow;
        var project = new Workstream { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, Name = "Memory delivery",
            AccountableManagerOrganizationUserId = fixture.EmployeeId, Status = WorkstreamStatus.Active };
        db.Add(project);
        board.WorkstreamId = project.Id; board.ManagerOrganizationUserId = fixture.EmployeeId;
        db.ProjectParticipants.Add(new ProjectParticipant { OrganizationId = fixture.OrganizationId, WorkstreamId = project.Id,
            OrganizationUserId = fixture.EmployeeId, AddedByOrganizationUserId = fixture.HumanId, JoinedAt = now.AddMinutes(-1) });
        var approved = new WorkDeliveryScopeSnapshot(scope, item.Id, board.Id, item.PlanningRevision, [],
            new WorkItemPlanningSpecification(["Review the exact candidate"], ["The candidate passes review"]),
            [new WorkStageAssignment(key == "manager-review" ? "technical-review" : key, "AgentInstallation", fixture.EmployeeId, fixture.InstallationId)]);
        var scopes = JsonSerializer.Serialize(new[] { approved }, ReadEvidenceJson);
        var candidate = JsonSerializer.Serialize(new WorkDeliveryCandidate(new string('a', 64), 1, [], []), ReadEvidenceJson);
        var plan = new WorkDeliveryPlan { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, WorkstreamId = project.Id,
            ManagerOrganizationUserId = fixture.EmployeeId, Name = "Scoped review", Status = "Active", ScopeRevision = 1, ScopesJson = scopes };
        var execution = new WorkDeliveryExecution { Id = Guid.NewGuid(), Plan = plan, Scope = scope, WorkItemId = item.Id,
            BoardId = board.Id, ScopeRevision = 1, CurrentStageKey = key, Status = "Running", CandidateJson = candidate };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), DeliveryExecution = execution, StageKey = key,
            StageType = WorkOrchestrationStageType.AgentExecution, PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            OrganizationUserId = fixture.EmployeeId, AgentInstallationId = fixture.InstallationId,
            Status = WorkStageExecutionStatus.Running, CreatedAt = now, UpdatedAt = now };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecution = stage, AgentWorkItemId = workId,
            Attempt = 1, IdempotencyKey = Guid.NewGuid().ToString("D"), Status = WorkExecutionAttemptStatus.Running, CreatedAt = now };
        db.AddRange(plan, execution, stage, attempt);
        var work = await db.AgentWorkItems.SingleAsync(x => x.Id == workId);
        work.SourceType = "WorkDeliveryStage"; work.SourceId = stage.Id.ToString("D");
        work.Name = WorkManagementCapabilityNames.ExecutionRunV2;
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync(ScopedCaseConsumerAuthority.AuthorityTriggers);
            await transaction.CommitAsync();
        }
        return new(stage.Id, attempt.Id, execution.Id, plan.Id, project.Id, board.Id, scopes, candidate);
    }

    [MemoryPostgresTheory]
    [InlineData("Story", "quality")]
    [InlineData("Story", "build-readiness")]
    [InlineData("Epic", "technical-review")]
    [InlineData("Epic", "manager-review")]
    public async Task ScopedAudienceCaseDeliveryReadUsesRealAggregateIdentityAndSurvivesOrdinaryProgress(string scope, string key)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        await SeedScopedDeliveryConsumerAsync(fixture, scoped, work.Id, scope, key);
        await using var db = fixture.Context();
        var read = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.True(read.Succeeded, read.Error); Assert.Contains("Alice", read.Payload.ToStringUtf8());
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await db.WorkDeliveryPlans.ExecuteUpdateAsync(x => x.SetProperty(p => p.Revision, 7).SetProperty(p => p.Name, "Renamed plan"));
        await db.WorkDeliveryExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.Revision, 9).SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
        await db.WorkStageExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.LastSummary, "Review in progress"));
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.Title, "Renamed case"));
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        Assert.Empty(await db.WorkSprintExecutions.ToArrayAsync());
    }

    private static async Task ChangeScopedDeliveryAuthorityAsync(CSweetDbContext db, DurabilityFixture fixture,
        ScopedAudienceFixture scoped, ScopedDeliveryFixture delivery, string change, bool restore)
    {
        switch (change)
        {
            case "plan": await db.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, restore ? "Active" : "Paused")); break;
            case "scope-revision": await db.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.ScopeRevision, restore ? 1 : 2)); break;
            case "scope-assignment": await db.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.ScopesJson,
                restore ? delivery.Scopes : delivery.Scopes.Replace(fixture.EmployeeId.ToString("D"), fixture.HumanId.ToString("D")))); break;
            case "scope-identity": await db.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.ScopesJson,
                restore ? delivery.Scopes : delivery.Scopes.Replace(scoped.Item!.Value.ToString("D"), Guid.Empty.ToString("D")))); break;
            case "candidate": await db.WorkDeliveryExecutions.Where(x => x.Id == delivery.Execution).ExecuteUpdateAsync(x => x.SetProperty(p => p.CandidateJson,
                restore ? delivery.Candidate : delivery.Candidate.Replace(new string('a', 64), new string('b', 64)))); break;
            case "delivery": await db.WorkDeliveryExecutions.Where(x => x.Id == delivery.Execution).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, restore ? "Running" : "Superseded")); break;
            case "current-stage": await db.WorkDeliveryExecutions.Where(x => x.Id == delivery.Execution).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentStageKey, restore ? "quality" : "technical-review")); break;
            case "stage": await db.WorkStageExecutions.Where(x => x.Id == delivery.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, restore ? WorkStageExecutionStatus.Running : WorkStageExecutionStatus.Failed)); break;
            case "reviewer": await db.WorkStageExecutions.Where(x => x.Id == delivery.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.OrganizationUserId, restore ? fixture.EmployeeId : fixture.HumanId)); break;
            case "attempt": await db.WorkExecutionAttempts.Where(x => x.Id == delivery.Attempt).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, restore ? WorkExecutionAttemptStatus.Running : WorkExecutionAttemptStatus.Completed)); break;
            case "participant": await db.ProjectParticipants.Where(x => x.WorkstreamId == delivery.Project).ExecuteUpdateAsync(x => x.SetProperty(p => p.RemovedAt, restore ? (DateTimeOffset?)null : DateTimeOffset.UtcNow)); break;
            case "planning": await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.PlanningRevision, restore ? 1 : 2)); break;
            case "board-project": await db.WorkBoards.Where(x => x.Id == delivery.Board).ExecuteUpdateAsync(x => x.SetProperty(p => p.WorkstreamId, restore ? delivery.Project : (Guid?)null)); break;
            case "grant": await db.ScopedActionGrants.Where(x => x.SubjectId == fixture.InstallationId && x.Action == WorkItemActions.ReadComments)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAt, restore ? (DateTimeOffset?)null : DateTimeOffset.UtcNow)); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    [MemoryPostgresTheory]
    [InlineData("plan", false)] [InlineData("plan", true)]
    [InlineData("scope-revision", false)] [InlineData("scope-revision", true)]
    [InlineData("scope-assignment", false)] [InlineData("scope-assignment", true)]
    [InlineData("scope-identity", false)] [InlineData("scope-identity", true)]
    [InlineData("candidate", false)] [InlineData("candidate", true)]
    [InlineData("delivery", false)] [InlineData("delivery", true)]
    [InlineData("current-stage", false)] [InlineData("current-stage", true)]
    [InlineData("stage", false)] [InlineData("stage", true)]
    [InlineData("reviewer", false)] [InlineData("reviewer", true)]
    [InlineData("attempt", false)] [InlineData("attempt", true)]
    [InlineData("participant", false)] [InlineData("participant", true)]
    [InlineData("planning", false)] [InlineData("planning", true)]
    [InlineData("board-project", false)] [InlineData("board-project", true)]
    [InlineData("grant", false)] [InlineData("grant", true)]
    public async Task ScopedAudienceCaseDeliveryRejectsRevocationAndChangeThenRestore(string change, bool restore)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var delivery = await SeedScopedDeliveryConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default)).Succeeded);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await ChangeScopedDeliveryAuthorityAsync(db, fixture, scoped, delivery, change, false);
        // A replacement with the same scope revision is still a valid current candidate.
        // It must invalidate the old context receipt; memory authorization does not
        // replace delivery's separate candidate/commit execution grant validation.
        if (change != "candidate")
        {
            var denied = await ReadHandler(fixture, db).HandleAsync(session,
                ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
            Assert.False(denied.Succeeded); Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        }
        Assert.Single(await db.AgentMemoryReadReceipts.ToArrayAsync());
        if (restore) await ChangeScopedDeliveryAuthorityAsync(db, fixture, scoped, delivery, change, true);
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData("release")]
    [InlineData("payload")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-capability")]
    [InlineData("wrong-source")]
    [InlineData("stale-candidate")]
    [InlineData("duplicate-scope")]
    [InlineData("manager")]
    public async Task ScopedAudienceCaseDeliveryCannotInferAuthorityFromReleasePayloadOrStaleBindings(string invalid)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var delivery = await SeedScopedDeliveryConsumerAsync(fixture, scoped, work.Id,
            invalid == "manager" ? "Epic" : "Story", invalid == "manager" ? "manager-review" : "quality");
        await using var db = fixture.Context();
        var current = await db.AgentWorkItems.SingleAsync(x => x.Id == work.Id);
        current.ProtectedPayload = JsonSerializer.SerializeToUtf8Bytes(new { workItemId = scoped.Item, caseId = scoped.Item, stageExecutionId = delivery.Stage });
        if (invalid == "release") await db.WorkDeliveryExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.WorkItemId, (Guid?)null).SetProperty(p => p.Scope, "Release"));
        else if (invalid == "payload") current.SourceId = Guid.NewGuid().ToString("D");
        else if (invalid == "wrong-kind") current.Kind = AgentWorkKind.Event;
        else if (invalid == "wrong-capability") current.Name = "fixture";
        else if (invalid == "wrong-source") current.SourceType = "WorkStageExecution";
        else if (invalid == "stale-candidate") await db.WorkDeliveryExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.CandidateJson, "{\"digest\":\"" + new string('a', 64) + "\",\"scopeRevision\":2}"));
        else if (invalid == "duplicate-scope") await db.WorkDeliveryPlans.ExecuteUpdateAsync(x => x.SetProperty(p => p.ScopesJson, "[" + delivery.Scopes[1..^1] + "," + delivery.Scopes[1..^1] + "]"));
        else if (invalid == "manager") await db.WorkBoards.ExecuteUpdateAsync(x => x.SetProperty(p => p.ManagerOrganizationUserId, fixture.HumanId));
        await db.SaveChangesAsync();
        var denied = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.False(denied.Succeeded); Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ScopedAudienceCaseDeliveryReplacementRemovalCannotReviveAnOldReceipt(bool replaceStage)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var delivery = await SeedScopedDeliveryConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default)).Succeeded);
        var replacement = Guid.NewGuid();
        if (replaceStage) db.WorkStageExecutions.Add(new WorkStageExecution { Id = replacement, DeliveryExecutionId = delivery.Execution,
            StageKey = "quality", Traversal = 1, StageType = WorkOrchestrationStageType.AgentExecution, Status = WorkStageExecutionStatus.Completed });
        else db.WorkExecutionAttempts.Add(new WorkExecutionAttempt { Id = replacement, StageExecutionId = delivery.Stage,
            Attempt = 2, IdempotencyKey = Guid.NewGuid().ToString("D"), Status = WorkExecutionAttemptStatus.Completed });
        await db.SaveChangesAsync();
        var denied = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.False(denied.Succeeded); Assert.DoesNotContain("Alice", denied.Payload.ToStringUtf8());
        if (replaceStage) await db.WorkStageExecutions.Where(x => x.Id == replacement).ExecuteDeleteAsync();
        else await db.WorkExecutionAttempts.Where(x => x.Id == replacement).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ScopedAudienceCaseDeliveryContentionIsRetryableWithoutDiscardingAuthorizedContext(bool plan)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var delivery = await SeedScopedDeliveryConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default)).Succeeded);
        await using var writer = fixture.Context();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        if (plan) await writer.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Paused"));
        else await writer.WorkDeliveryExecutions.Where(x => x.Id == delivery.Execution).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Superseded"));
        await using (var read = await db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
            await read.RollbackAsync();
        }
        Assert.Null((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
        await transaction.RollbackAsync();
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseDeliveryMigrationBackfillsAndProtectsPlanAndExecutionGenerations()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (_, work) = await SeedBrokerReadLeaseAsync(fixture);
        var delivery = await SeedScopedDeliveryConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        var migration = new ScopedCaseConsumerAuthority(); var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await using (var upgrade = await db.Database.BeginTransactionAsync())
        {
            foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
            await upgrade.CommitAsync();
        }
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(4, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM \"MemoryCaseConsumerAuthority\"").SingleAsync());
        await db.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Paused"));
        await db.WorkDeliveryPlans.Where(x => x.Id == delivery.Plan).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, "Active"));
        await db.WorkDeliveryExecutions.Where(x => x.Id == delivery.Execution).ExecuteUpdateAsync(x => x.SetProperty(p => p.CandidateJson, delivery.Candidate.Replace(new string('a', 64), new string('b', 64))));
        foreach (var sql in new[] { "UPDATE \"MemoryCaseConsumerAuthority\" SET \"Revision\"=1 WHERE \"Kind\" IN ('Plan','Delivery')",
            "DELETE FROM \"MemoryCaseConsumerAuthority\" WHERE \"Kind\"='Plan'", "TRUNCATE \"WorkDeliveryPlans\" CASCADE" })
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("discard execution revocation history", failure.MessageText);
    }
}
