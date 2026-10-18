using System.Reflection;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class DeliveryDispatchRecoveryTests
{
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, CSweet.Agent.SDK.ProjectHealthEvents.OwnerReportRequested)]
    [InlineData(false, false, "production-capability")]
    public async Task AggregateDispatchCommitsBeforeActivationAndRecoversUntouchedInboxAfterLostTracking(bool legacyOrphan, bool expired, string? leasedName)
    {
        await using var f = await HierarchicalDeliveryServiceTests.Fixture.Create(true);
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), ManifestJson = """{"manifestVersion":"2.0","protocol":{"minVersion":"2.0","maxVersion":"2.x"},"rolePolicy":{"requiresProject":true},"provides":[{"name":"work.execution.run"}]}""" };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), PackageVersion = package, BusinessId = f.Org.ToString(), SetupState = PluginSetupState.Ready };
        var reviewer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, DisplayName = "Aggregate QA",
            AgentInstallationId = installation.Id, EmployeeType = EmployeeType.Agent, IsActive = true };
        f.Db.AddRange(installation, reviewer,
            new ProjectParticipant { OrganizationId = f.Org, WorkstreamId = f.Project, OrganizationUserId = reviewer.Id, AddedByOrganizationUserId = f.Manager.Id },
            new WorkstreamTeamAssignmentRecord { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project, TeamId = f.Board.TeamId!.Value },
            new TeamMembership { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = f.Board.TeamId!.Value, OrganizationUserId = reviewer.Id });
        f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation.Id, ScopeKind = GrantScopeKind.Board, ScopeId = f.Board.Id, Action = CSweet.Contracts.WorkManagement.WorkItemActions.Read });
        await f.Db.SaveChangesAsync();
        var request = f.Request();
        request = request with { TicketWorkflows = request.TicketWorkflows.Select(scope => scope.Scope == "Story" ? scope with {
            Steps = [new("quality","quality",new("quality", "AgentInstallation", reviewer.Id, installation.Id))] } : scope).ToArray() };
        await f.Activate(await f.Configure(request)); await f.CompleteTasks();
        var inbox = new AgentWorkInbox(f.Db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        var runtime = DispatchProxy.Create<IAgentRuntimeManager, RuntimeProxy>();
        var activations = 0;
        ((RuntimeProxy)runtime).Activate = () => {
            activations++;
            var saved = f.Db.WorkDeliveryExecutions.AsNoTracking().Single(x => x.Scope == "Story");
            Assert.Equal("Running", saved.Status);
            Assert.Single(f.Db.WorkExecutionAttempts.AsNoTracking().Where(x => x.AgentWorkItemId != null));
            f.Db.ChangeTracker.Clear(); // Reproduces runtime preparation's degraded-mode reset.
        };
        var service = new WorkDeliveryService(f.Db, new ScopedActionAuthorizationService(f.Db), f.Client, TimeProvider.System, inbox, runtime);
        if (leasedName is not null)
        {
            f.Db.AgentWorkItems.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = installation.Id,
                Name = leasedName, Kind = leasedName == CSweet.Agent.SDK.ProjectHealthEvents.OwnerReportRequested ? AgentWorkKind.Event : AgentWorkKind.Capability,
                Status = AgentWorkStatus.Leased, CreatedAt = DateTimeOffset.UtcNow,
                AvailableAt = DateTimeOffset.UtcNow, DeadlineAt = DateTimeOffset.UtcNow.AddHours(1), SourceType = "capacity-regression" });
            await f.Db.SaveChangesAsync();
        }
        await service.PulseAsync();
        if (leasedName == "production-capability")
        {
            Assert.Equal("Ready", (await f.Db.WorkDeliveryExecutions.SingleAsync(x => x.Scope == "Story")).Status);
            Assert.Empty(await f.Db.WorkExecutionAttempts.Where(x => x.AgentWorkItemId != null).ToArrayAsync());
            Assert.Equal(0, activations); return;
        }
        Assert.True(await f.Db.WorkDeliveryExecutions.AllAsync(x => x.Status != "Blocked"),
            string.Join("; ", await f.Db.WorkDeliveryExecutions.Select(x => x.BlockedReason).ToListAsync()));
        var work = Assert.Single(await f.Db.AgentWorkItems.Where(x => x.SourceType == "WorkDeliveryStage").ToListAsync());
        var attempt = Assert.Single(await f.Db.WorkExecutionAttempts.Where(x => x.AgentWorkItemId == work.Id).ToListAsync());
        var stage = await f.Db.WorkStageExecutions.SingleAsync(x => x.Id == attempt.StageExecutionId);
        if (legacyOrphan)
        {
            // Historical dispatch: inbox saved, later tracking reset discarded its attempt/cursor.
            f.Db.Remove(attempt); stage.Status = WorkStageExecutionStatus.Pending;
            var execution = await f.Db.WorkDeliveryExecutions.SingleAsync(x => x.Id == stage.DeliveryExecutionId);
            if (expired) work.DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            execution.Status = "Ready"; await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
            await service.PulseAsync();
            if (expired)
            {
                Assert.Equal(AgentWorkStatus.Cancelled, (await f.Db.AgentWorkItems.SingleAsync()).Status);
                Assert.Equal("Blocked", (await f.Db.WorkDeliveryExecutions.SingleAsync(x => x.Id == execution.Id)).Status);
                Assert.Empty(await f.Db.WorkExecutionAttempts.Where(x => x.AgentWorkItemId == work.Id).ToArrayAsync());
                Assert.Equal(1, activations); return;
            }
            var recovered = Assert.Single(await f.Db.WorkExecutionAttempts.Where(x => x.AgentWorkItemId == work.Id).ToListAsync());
            Assert.Equal(attempt.Id, recovered.Id); Assert.Equal(attempt.IdempotencyKey, recovered.IdempotencyKey);
        }
        await service.PulseAsync();
        Assert.Single(await f.Db.AgentWorkItems.Where(x => x.SourceType == "WorkDeliveryStage").ToListAsync());
        Assert.Equal(legacyOrphan ? 2 : 1, activations);
    }

    [Fact]
    public void AggregateCursorUsesStageIdentityAndTraversalEvenWhenCollectionsAndTimesAreUnordered()
    {
        var now = DateTimeOffset.UtcNow;
        var current = new WorkStageExecution { Id = Guid.NewGuid(), StageKey = "technical-review", Traversal = 1, CreatedAt = now };
        var execution = new WorkDeliveryExecution { CurrentStageKey = "technical-review", Stages = [current,
            new() { Id = Guid.NewGuid(), StageKey = "quality", CreatedAt = now },
            new() { Id = Guid.NewGuid(), StageKey = "technical-review", Traversal = 0, CreatedAt = now }] };
        Assert.Same(current, WorkDeliveryService.CurrentDeliveryStage(execution));
        execution.Stages.Add(new() { Id = Guid.NewGuid(), StageKey = "technical-review", Traversal = 1 });
        Assert.Throws<InvalidOperationException>(() => WorkDeliveryService.CurrentDeliveryStage(execution));
    }

    public class RuntimeProxy : DispatchProxy
    {
        public Action Activate = () => { };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IAgentRuntimeManager.EnsureRuntimeQueuedAsync), method!.Name);
            Activate(); return Task.FromResult(true);
        }
    }
}
