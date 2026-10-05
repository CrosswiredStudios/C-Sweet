using System.Reflection;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shared = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class WorkDispatchRecoveryTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    public async Task Dispatch_persists_attempt_and_positive_revision_before_runtime_wake(long revision, long expected)
    {
        await using var db = CreateDb();
        var state = await Seed(db, revision);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var runtime = new Runtime(db);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, runtime, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        await Dispatch(orchestrator, state);
        await orchestrator.PulseAsync();
        db.ChangeTracker.Clear();
        var attempt = Assert.Single(await db.WorkExecutionAttempts.ToListAsync());
        var work = Assert.Single(await db.AgentWorkItems.ToListAsync());
        var assignment = inbox.ReadPayload(work).Deserialize<Shared.WorkExecutionAssignmentV1>(JsonOptions)!;
        Assert.Equal(attempt.Id, assignment.AttemptId);
        Assert.Equal(work.Id, attempt.AgentWorkItemId);
        Assert.Equal(expected, assignment.AssignmentRevision);
        Assert.Equal(expected, (await db.CoreWorkTasks.SingleAsync()).AssignmentRevision);
        Assert.Equal(0, assignment.Traversal); // Initial traversal is zero; attempts are one-based.
        Assert.Equal(1, assignment.Attempt);
        Assert.Equal(WorkStageExecutionStatus.Dispatching, (await db.WorkStageExecutions.SingleAsync()).Status);
        Assert.Equal(1, runtime.Wakes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detached_inbox_work_is_recovered_once_without_redispatch_and_mismatched_identity_is_rejected(bool mismatched)
    {
        await using var db = CreateDb();
        var state = await Seed(db, 0);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var assignment = JsonSerializer.Deserialize<Shared.WorkExecutionAssignmentV1>("{}")! with
        {
            OrganizationId = state.Execution.OrganizationId, BoardId = state.Execution.BoardId,
            SprintId = state.Execution.SprintId, SprintExecutionId = state.Execution.Id,
            ItemExecutionId = state.Stage.ItemExecutionId, ItemId = state.Stage.ItemExecution!.WorkItemId,
            StageExecutionId = mismatched ? Guid.NewGuid() : state.Stage.Id, StageKey = state.Stage.StageKey,
            Traversal = 0, Attempt = 1, AttemptId = Guid.NewGuid(), AssignmentRevision = 0,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5), Item = JsonSerializer.SerializeToElement(new {}),
            Input = JsonSerializer.SerializeToElement(new {}), PriorOutcomes = [], Evidence = []
        };
        var key = $"orchestration:{state.Execution.Id:N}:{state.Stage.ItemExecutionId:N}:{state.Stage.StageKey}:0:1";
        var work = await inbox.EnqueueAsync(state.Execution.OrganizationId.ToString(), state.Installation.Id,
            AgentWorkKind.Capability, Shared.WorkManagementCapabilityNames.ExecutionRunV1,
            JsonSerializer.SerializeToElement(assignment, JsonOptions), key, assignment.Deadline,
            sourceType: "WorkStageExecution", sourceId: state.Stage.Id.ToString("D"));
        var runtime = new Runtime(db);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, runtime, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        if (mismatched)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch(orchestrator, state));
            Assert.Empty(db.WorkExecutionAttempts);
            return;
        }
        await Dispatch(orchestrator, state);
        await orchestrator.PulseAsync();
        Assert.Single(db.AgentWorkItems);
        var attempt = Assert.Single(db.WorkExecutionAttempts);
        Assert.Equal(assignment.AttemptId, attempt.Id);
        Assert.Equal(work.Id, attempt.AgentWorkItemId);
        Assert.Single(db.WorkOrchestrationEvents.Where(x => x.EventType == "attempt.dispatch.recovered"));
        Assert.Equal(0, runtime.Wakes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Relational_dispatch_commits_inbox_attempt_and_grants_together(bool failAfterEnqueue)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync();
        var failure = new FailSecondSave();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseSqlite(connection).AddInterceptors(failure).Options);
        await db.Database.EnsureCreatedAsync();
        var state = await Seed(db, 0);
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var runtime = new Runtime(db);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, runtime, [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        failure.Armed = failAfterEnqueue;
        if (failAfterEnqueue)
            await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch(orchestrator, state));
        else await Dispatch(orchestrator, state);
        db.ChangeTracker.Clear();
        Assert.Equal(failAfterEnqueue ? 0 : 1, await db.AgentWorkItems.CountAsync());
        Assert.Equal(failAfterEnqueue ? 0 : 1, await db.WorkExecutionAttempts.CountAsync());
        Assert.Equal(!failAfterEnqueue, await db.ScopedActionGrants.AnyAsync());
        Assert.Equal(failAfterEnqueue ? 0 : 1, await db.ApplicationRealtimeOutbox.CountAsync());
        Assert.Equal(failAfterEnqueue ? 0 : 1, (await db.CoreWorkTasks.SingleAsync()).AssignmentRevision);
        Assert.Equal(failAfterEnqueue ? 0 : 1, runtime.Wakes);
    }

    [Fact]
    public async Task Retried_stage_carries_the_managers_direction_to_the_worker()
    {
        // A worker asked to try again must hear why, as a teammate would.
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        var managerId = Guid.NewGuid();
        (await db.WorkBoards.SingleAsync()).ManagerOrganizationUserId = managerId;
        foreach (var (actor, reason, at) in new[]
        {
            (managerId, "Report unavailable desktop measurements honestly.", DateTimeOffset.UtcNow.AddMinutes(-2)),
            (Guid.NewGuid(), "Architect guidance was linked and consumed.", DateTimeOffset.UtcNow.AddMinutes(-1))
        })
            db.WorkOrchestrationEvents.Add(new WorkOrchestrationEvent
            {
                Id = Guid.NewGuid(), OrganizationId = state.Execution.OrganizationId, BoardId = state.Execution.BoardId,
                SprintExecutionId = state.Execution.Id, ItemExecutionId = state.Stage.ItemExecutionId, StageExecutionId = state.Stage.Id,
                EventType = "stage.retry.requested", OccurredAt = at,
                DataJson = JsonSerializer.Serialize(new { id = actor, reason, idempotencyKey = reason })
            });
        await db.SaveChangesAsync();
        var inbox = new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, new Runtime(db), [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        await Dispatch(orchestrator, state);
        var assignment = inbox.ReadPayload(Assert.Single(db.AgentWorkItems)).Deserialize<Shared.WorkExecutionAssignmentV1>(JsonOptions)!;
        var direction = Assert.Single(assignment.Evidence, x => x.Kind == "manager-direction");
        Assert.Equal("Report unavailable desktop measurements honestly.", direction.Value);
    }

    [Fact]
    public async Task Blocked_result_replaces_previous_attempt_error_on_stage_and_ticket()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        (await db.WorkBoards.SingleAsync()).WorkstreamId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider();
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, new Runtime(db), [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        await Dispatch(orchestrator, state);
        var attempt = Assert.Single(db.WorkExecutionAttempts);
        var work = Assert.Single(db.AgentWorkItems);
        const string currentError = "The deliverable is missing required section 'Toolchain Feasibility'.";
        state.Stage.LastError = "Previous assignment validation error.";
        var outcome = new Shared.WorkExecutionOutcomeV1(state.Stage.Id, attempt.Id,
            Shared.WorkExecutionDispositions.Blocked, "blocked", currentError,
            JsonSerializer.SerializeToElement(new {}), [], [currentError]);
        work.Status = AgentWorkStatus.Completed;
        work.ProtectedResult = protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(
            JsonSerializer.SerializeToUtf8Bytes(new AgentWorkCompletion(true, JsonSerializer.SerializeToElement(outcome, JsonOptions), null)));
        await db.SaveChangesAsync();
        await orchestrator.PulseAsync();
        Assert.Equal(WorkStageExecutionStatus.Blocked, state.Stage.Status);
        Assert.Equal(currentError, state.Stage.LastError);
        Assert.Equal(currentError, state.Stage.ItemExecution!.BlockedReason);
        Assert.Equal(currentError, state.Stage.ItemExecution.WorkItem!.BlockReason);
        var wake = Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == Shared.WorkstreamEventNames.WorkItemChangedV1).ToListAsync());
        Assert.Contains("stage.recovery-required", wake.DataJson);
        await orchestrator.PulseAsync();
        Assert.Single(await db.AgentPlatformEventOutbox.Where(x =>
            x.EventType == Shared.WorkstreamEventNames.WorkItemChangedV1).ToListAsync());
    }
    [Fact]
    public async Task Completed_worker_result_advances_once_and_survives_a_fresh_reconciliation()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        state.Policy.Stages.Add(new WorkOrchestrationStage
        {
            Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            Key = "producer-review", Name = "Review", Type = WorkOrchestrationStageType.ManagerApproval
        });
        state.Policy.Transitions.Add(new WorkOrchestrationTransition
        {
            Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            FromStageKey = state.Stage.StageKey, OutcomeCode = "completed", ToStageKey = "producer-review"
        });
        db.Add(state.Policy.Stages.Last());
        db.Add(state.Policy.Transitions.Last());
        await db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider();
        var inbox = new AgentWorkInbox(db, protection, TimeProvider.System);
        var runtime = new Runtime(db);
        var orchestrator = new WorkOrchestrator(db, inbox, null!, runtime, [], TimeProvider.System,
            NullLogger<WorkOrchestrator>.Instance);
        await Dispatch(orchestrator, state);
        var attempt = Assert.Single(db.WorkExecutionAttempts);
        var work = Assert.Single(db.AgentWorkItems);
        var outcome = new Shared.WorkExecutionOutcomeV1(state.Stage.Id, attempt.Id,
            Shared.WorkExecutionDispositions.Completed, "completed", "Plan submitted.",
            JsonSerializer.SerializeToElement(new { artifactId = Guid.NewGuid() }), [], []);
        work.Status = AgentWorkStatus.Completed;
        work.ProtectedResult = protection.CreateProtector("CSweet.AgentWorkInbox.v1").Protect(
            JsonSerializer.SerializeToUtf8Bytes(new AgentWorkCompletion(true,
                JsonSerializer.SerializeToElement(outcome, JsonOptions), null)));
        await db.SaveChangesAsync();

        // Pulse catches reconciliation errors: verify persisted state, not only tracked mutations.
        await orchestrator.PulseAsync();
        db.ChangeTracker.Clear();
        var persisted = await db.WorkExecutionAttempts.SingleAsync();
        Assert.Equal(WorkExecutionAttemptStatus.Completed, persisted.Status);
        Assert.NotNull(persisted.CompletedAt);
        Assert.Equal(outcome.Summary, JsonSerializer.Deserialize<Shared.WorkExecutionOutcomeV1>(persisted.ResultJson!, JsonOptions)!.Summary);
        Assert.Equal(WorkStageExecutionStatus.Completed,
            (await db.WorkStageExecutions.SingleAsync(x => x.Id == state.Stage.Id)).Status);
        var review = await db.WorkStageExecutions.SingleAsync(x => x.StageKey == "producer-review");
        Assert.Equal(WorkStageExecutionStatus.WaitingForApproval, review.Status);
        Assert.Equal("producer-review", (await db.WorkItemExecutions.SingleAsync()).CurrentStageKey);

        await orchestrator.PulseAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(review.Id, (await db.WorkStageExecutions.SingleAsync(x => x.StageKey == "producer-review")).Id);
        Assert.Equal(2, await db.WorkStageExecutions.CountAsync());
        Assert.Single(await db.WorkOrchestrationEvents.Where(x => x.EventType == "attempt.result.accepted").ToListAsync());
        Assert.Single(await db.AgentWorkItems.ToListAsync());
        Assert.Equal(1, runtime.Wakes);
    }
    [Fact]
    public async Task Terminal_completion_is_durable_even_when_a_dependent_dispatch_fails()
    {
        await using var db = CreateDb();
        var state = await Seed(db, 1);
        state.Stage.StageType = WorkOrchestrationStageType.Terminal;
        state.Policy.Stages.Single().Type = WorkOrchestrationStageType.Terminal;
        state.Policy.Stages.Single().IsSuccessfulTerminal = true;
        var definition = new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = state.Policy.Id,
            Key = "dependent-work", Name = "Dependent work", Type = WorkOrchestrationStageType.AgentExecution };
        state.Policy.Stages.Add(definition); db.Add(definition);
        var dependent = new WorkTask { Id = Guid.NewGuid(), OrganizationId = state.Execution.OrganizationId,
            BoardId = state.Execution.BoardId, Identifier = "GAME-2" };
        dependent.Dependencies.Add(new WorkItemDependency { WorkItemId = dependent.Id,
            DependsOnWorkItemId = state.Stage.ItemExecution!.WorkItemId });
        var next = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = state.Execution.Id,
            WorkItemId = dependent.Id, WorkItem = dependent, Status = WorkItemExecutionStatus.Pending };
        next.Stages.Add(new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = next.Id,
            StageKey = definition.Key, StageType = definition.Type, Status = WorkStageExecutionStatus.Pending });
        state.Execution.Items.Add(next); db.Add(next); db.Add(dependent);
        await db.SaveChangesAsync();
        var orchestrator = new WorkOrchestrator(db, new AgentWorkInbox(db, new EphemeralDataProtectionProvider(), TimeProvider.System),
            null!, new Runtime(db), [], TimeProvider.System, NullLogger<WorkOrchestrator>.Instance);
        // The dependent deliberately has no installation, so dispatch fails after its dependency completes.
        await orchestrator.PulseAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(WorkItemExecutionStatus.Completed,
            (await db.WorkItemExecutions.SingleAsync(x => x.Id == state.Stage.ItemExecutionId)).Status);
        Assert.Equal(WorkTaskStatus.Completed,
            (await db.CoreWorkTasks.SingleAsync(x => x.Id == state.Stage.ItemExecution!.WorkItemId)).Status);
        Assert.Empty(await db.AgentWorkItems.ToListAsync());
    }
    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        private int calls;
        private long sequence;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // SQLite cannot generate the PostgreSQL outbox identity column.
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<CSweet.Domain.Notifications.ApplicationRealtimeOutboxItem>()
                         .Where(x => x.State == EntityState.Added && x.Entity.Sequence == 0))
                entry.Entity.Sequence = ++sequence;
            if (Armed && ++calls == 2) throw new InvalidOperationException("Injected failure after inbox save.");
            return ValueTask.FromResult(result);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    private static async Task Dispatch(WorkOrchestrator orchestrator, State state) => await (Task)typeof(WorkOrchestrator)
        .GetMethod("DispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(orchestrator, [state.Execution, state.Policy, state.Stage, DateTimeOffset.UtcNow, CancellationToken.None])!;
    private static async Task<State> Seed(CSweetDbContext db, long assignmentRevision)
    {
        var org = Guid.NewGuid();
        var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = org.ToString() };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, Name = "Delivery" };
        var policy = new WorkOrchestrationPolicyRevision { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Name = "Delivery" };
        var definition = new WorkOrchestrationStage { Id = Guid.NewGuid(), PolicyRevisionId = policy.Id, Key = "specialist-execution", Name = "Execute", Type = WorkOrchestrationStageType.AgentExecution, TimeoutSeconds = 300 };
        policy.Stages.Add(definition);
        var task = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, Identifier = "GAME-1", AssignmentRevision = assignmentRevision };
        var execution = new WorkSprintExecution { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, SprintId = Guid.NewGuid(), PolicyRevisionId = policy.Id, Status = WorkSprintExecutionStatus.Active };
        var item = new WorkItemExecution { Id = Guid.NewGuid(), SprintExecutionId = execution.Id, WorkItemId = task.Id, WorkItem = task, Status = WorkItemExecutionStatus.Pending };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = item.Id, ItemExecution = item, StageKey = definition.Key, StageType = definition.Type, Status = WorkStageExecutionStatus.Pending, AgentInstallationId = installation.Id, PrincipalKind = WorkOrchestrationPrincipalKind.AgentInstallation };
        item.Stages.Add(stage); execution.Items.Add(item);
        execution.AssignmentSnapshotJson = JsonSerializer.Serialize(new[] { new { workItemId = task.Id, stageKey = stage.StageKey, principalKind = 1, agentInstallationId = installation.Id } });
        db.AddRange(installation, board, policy, task, execution);
        await db.SaveChangesAsync();
        return new(installation, execution, policy, stage);
    }
    private sealed record State(AgentInstallation Installation, WorkSprintExecution Execution, WorkOrchestrationPolicyRevision Policy, WorkStageExecution Stage);
    private sealed class Runtime(CSweetDbContext db) : IAgentRuntimeManager
    {
        public int Wakes { get; private set; }
        public async Task<bool> EnsureRuntimeQueuedAsync(Guid id, string reason, bool interactive = false, CancellationToken cancellationToken = default)
        {
            Assert.Single(await db.WorkExecutionAttempts.AsNoTracking().ToListAsync(cancellationToken));
            Assert.NotEmpty(await db.ScopedActionGrants.AsNoTracking().ToListAsync(cancellationToken));
            Wakes++; return true;
        }
        public Task<bool> RestartRuntimeAsync(Guid id, string reason, bool interactive = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
