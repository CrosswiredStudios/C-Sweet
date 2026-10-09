using CSweet.AgentHost.Broker;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence.Migrations;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class AgentMemoryServiceTests
{
    private sealed record ScopedStageFixture(Guid Stage, Guid Attempt, Guid Item, Guid Sprint);

    private static async Task<ScopedStageFixture> SeedScopedStageConsumerAsync(DurabilityFixture fixture,
        ScopedAudienceFixture scoped, Guid workId)
    {
        await using var db = fixture.Context();
        var board = await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).Select(x => x.BoardId!.Value).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var policy = new WorkOrchestrationPolicy { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, BoardId = board };
        var revision = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId,
            BoardId = board, Policy = policy, Revision = 1, InitialStageKey = "build" };
        var sprint = new WorkSprint { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, BoardId = board, Name = "Memory test sprint" };
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = fixture.OrganizationId, BoardId = board,
            SprintId = sprint.Id, PolicyRevisionId = revision.Id, StartedByOrganizationUserId = fixture.HumanId,
            Status = WorkSprintExecutionStatus.Active, StartedAt = now, UpdatedAt = now };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = scoped.Item!.Value, SprintExecution = execution,
            CurrentStageKey = "build", Status = WorkItemExecutionStatus.Running, CreatedAt = now, UpdatedAt = now };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecution = item, StageKey = "build",
            StageType = WorkOrchestrationStageType.AgentExecution, PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation,
            OrganizationUserId = fixture.EmployeeId, AgentInstallationId = fixture.InstallationId, Status = WorkStageExecutionStatus.Running,
            CreatedAt = now, UpdatedAt = now };
        var attempt = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecution = stage, AgentWorkItemId = workId,
            Attempt = 1, IdempotencyKey = Guid.NewGuid().ToString("D"), Status = WorkExecutionAttemptStatus.Running, CreatedAt = now };
        db.AddRange(policy, revision, sprint, execution, item, stage, attempt);
        var work = await db.AgentWorkItems.SingleAsync(x => x.Id == workId);
        work.SourceType = "WorkStageExecution"; work.SourceId = stage.Id.ToString("D");
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync(ScopedCaseConsumerAuthority.AuthorityTriggers);
            await transaction.CommitAsync();
        }
        return new(stage.Id, attempt.Id, item.Id, execution.Id);
    }

    [MemoryPostgresTheory]
    [InlineData("attempt", false)]
    [InlineData("attempt", true)]
    [InlineData("stage", false)]
    [InlineData("stage", true)]
    [InlineData("item", false)]
    [InlineData("item", true)]
    [InlineData("sprint", false)]
    [InlineData("sprint", true)]
    public async Task ScopedAudienceCaseStageConsumerSurvivesProgressButRejectsExecutionChangeAndRestore(string change, bool restore)
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var execution = await SeedScopedStageConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        var read = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.True(read.Succeeded, read.Error); Assert.Contains("Alice", read.Payload.ToStringUtf8());
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        await db.CoreWorkTasks.Where(x => x.Id == scoped.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.Title, "Renamed card"));
        await db.WorkSprintExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow).SetProperty(p => p.Revision, 12));
        await db.WorkStageExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.LastSummary, "Ordinary progress").SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
        await db.WorkItemExecutions.ExecuteUpdateAsync(x => x.SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow));
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        if (change == "attempt")
        {
            await db.WorkExecutionAttempts.Where(x => x.Id == execution.Attempt).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkExecutionAttemptStatus.Completed));
            if (restore) await db.WorkExecutionAttempts.Where(x => x.Id == execution.Attempt).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkExecutionAttemptStatus.Running));
        }
        else if (change == "stage")
        {
            await db.WorkStageExecutions.Where(x => x.Id == execution.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkStageExecutionStatus.Failed));
            if (restore) await db.WorkStageExecutions.Where(x => x.Id == execution.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkStageExecutionStatus.Running));
        }
        else if (change == "item")
        {
            await db.WorkItemExecutions.Where(x => x.Id == execution.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentStageKey, "other"));
            if (restore) await db.WorkItemExecutions.Where(x => x.Id == execution.Item).ExecuteUpdateAsync(x => x.SetProperty(p => p.CurrentStageKey, "build"));
        }
        else
        {
            await db.WorkSprintExecutions.Where(x => x.Id == execution.Sprint).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkSprintExecutionStatus.Paused));
            if (restore) await db.WorkSprintExecutions.Where(x => x.Id == execution.Sprint).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkSprintExecutionStatus.Active));
        }
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseStageCannotUseAnOlderAttemptEvenIfItStillSaysRunning()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var execution = await SeedScopedStageConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        db.WorkExecutionAttempts.Add(new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = execution.Stage,
            // A newer terminal attempt can coexist with stale Running state on the old attempt.
            Attempt = 2, IdempotencyKey = Guid.NewGuid().ToString("D"), Status = WorkExecutionAttemptStatus.Completed });
        await db.SaveChangesAsync();
        var read = await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default);
        Assert.False(read.Succeeded); Assert.DoesNotContain("Alice", read.Payload.ToStringUtf8());
        Assert.Empty(await db.AgentMemoryReadReceipts.ToArrayAsync());
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseStageAttemptRemovalCannotReviveAnOldReadReceipt()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var execution = await SeedScopedStageConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default)).Succeeded);
        await new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default);
        var newer = new WorkExecutionAttempt { Id = Guid.NewGuid(), StageExecutionId = execution.Stage,
            Attempt = 2, IdempotencyKey = Guid.NewGuid().ToString("D"), Status = WorkExecutionAttemptStatus.Completed };
        db.WorkExecutionAttempts.Add(newer); await db.SaveChangesAsync();
        await db.WorkExecutionAttempts.Where(x => x.Id == newer.Id).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<ProviderDispatchDeniedException>(() => new PlatformMemoryReadEvidence(db).AuthorizeDispatchAsync(session, work.Id, default));
        Assert.NotNull((await db.AgentRuntimeInstances.AsNoTracking().SingleAsync()).MemoryResetRequestedAt);
    }

    [MemoryPostgresFact]
    public async Task ScopedAudienceCaseExecutionContentionRequiresRefreshWithoutResettingAnAuthorizedRuntime()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (session, work) = await SeedBrokerReadLeaseAsync(fixture);
        var execution = await SeedScopedStageConsumerAsync(fixture, scoped, work.Id);
        await using var db = fixture.Context();
        Assert.True((await ReadHandler(fixture, db).HandleAsync(session,
            ReadRequest("search", new MemorySearchRequest(scoped.Episode.Partition, scoped.Episode.Scope, "Alice")), default)).Succeeded);
        await using var writer = fixture.Context();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.WorkStageExecutions.Where(x => x.Id == execution.Stage).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkStageExecutionStatus.Failed));
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
    public async Task ScopedAudienceCaseConsumerMigrationBackfillsPreservesAndProtectsExecutionHistory()
    {
        await using var fixture = await DurabilityFixture.CreateAsync(postgres: true);
        var scoped = await SeedScopedAudienceAsync(fixture, "Case");
        var (_, work) = await SeedBrokerReadLeaseAsync(fixture);
        var execution = await SeedScopedStageConsumerAsync(fixture, scoped, work.Id);
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
        await db.WorkExecutionAttempts.Where(x => x.Id == execution.Attempt).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, WorkExecutionAttemptStatus.Completed));
        foreach (var sql in new[] { "UPDATE \"MemoryCaseConsumerAuthority\" SET \"Revision\"=1", "DELETE FROM \"MemoryCaseConsumerAuthority\"", "TRUNCATE \"MemoryCaseConsumerAuthority\"" })
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        var failure = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        });
        Assert.Contains("discard execution revocation history", failure.MessageText);
        Assert.Equal(WorkExecutionAttemptStatus.Completed, (await db.WorkExecutionAttempts.AsNoTracking().SingleAsync(x => x.Id == execution.Attempt)).Status);
    }
}
