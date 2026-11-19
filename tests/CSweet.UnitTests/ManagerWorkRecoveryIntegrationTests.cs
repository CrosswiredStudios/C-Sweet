using CSweet.Agent.SDK;
using CSweet.Agent.Testing;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Application.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Fact, Trait("Category", "AgentHarness")]
    public async Task SharedManagerRecoveryResumesRetainedNativeWorkWithoutRecreatingItOrHoldingUnrelatedWork()
    {
        await using var db = CreateDb(); var setup = Seed(db);
        setup.FirstManager.EmployeeType = EmployeeType.Agent;
        setup.FirstManager.AgentInstallationId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var service = new PersonalTodoService(db, TimeProvider.System);
        var actor = new PersonalTodoActor(setup.FirstManager.Id, setup.FirstManager.AgentInstallationId);
        var retained = await service.AddAsync(setup.Organization.Id, actor, Add("Retained project planning", "retained", setup.Agent.Id));
        var independent = await service.AddAsync(setup.Organization.Id, actor, Add("Independent work", "independent", setup.Agent.Id));
        var task = await db.CoreWorkTasks.SingleAsync(x => x.Id == retained.Id);
        task.Status = WorkTaskStatus.Running;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await AgentTicketFeedback.RecordFailureAsync(db, task, setup.Agent.AgentInstallationId!.Value,
                $"old-attempt-{attempt}", "agent-failure:v1;code=agent.invalid_operation", true, DateTimeOffset.UtcNow.AddSeconds(attempt), default);
            await db.SaveChangesAsync();
        }
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<object, Wire.PersonalTodoDirectory>(Wire.PersonalTodoCapabilities.Read,
                (_, ct) => service.ListAsync(setup.Organization.Id, actor, cancellationToken: ct))
            .RegisterCapability<Wire.AddPersonalTodoItemRequest, Wire.PersonalTodoItem>(Wire.PersonalTodoCapabilities.Add,
                (r, ct) => service.AddAsync(setup.Organization.Id, actor, r, ct))
            .RegisterCapability<Wire.RequeuePersonalTodoItemRequest, Wire.PersonalTodoItem>(Wire.PersonalTodoCapabilities.Requeue,
                (r, ct) => service.RequeueAsync(setup.Organization.Id, actor, r, ct))
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(r.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) => {
                var now = DateTimeOffset.UtcNow;
                var saved = new AgentOperatingStateResponse(Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion, r.Status,
                    r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations, r.AttentionReviewId,
                    r.Payload, (states.GetValueOrDefault(r.StateKey)?.Revision ?? 0) + 1, now, now);
                states[r.StateKey] = saved; return Task.FromResult(saved);
            });
        var context = runtime.CreateContext(setup.Organization.Id.ToString(), setup.FirstManager.AgentInstallationId.ToString()!,
            new(setup.FirstManager.Id.ToString(), "Manager", null, null, null, [], null, null, null));
        var manager = new NativeRecoveryManager();
        AgentTestHost Host() => new(manager, runtime, context.Identity, setup.Organization.Id.ToString(), setup.FirstManager.AgentInstallationId.ToString()!);
        var delivery = Guid.NewGuid();
        await Host().DeliverEventAsync(Wire.WorkRecoveryEvents.Changed, new { itemId = retained.Id }, delivery);
        await Host().DeliverEventAsync(Wire.WorkRecoveryEvents.Changed, new { itemId = retained.Id }, delivery);
        var directory = await service.ListAsync(setup.Organization.Id, actor);
        var recovery = Assert.Single(directory.Boards.Single(b => b.OwnerOrganizationUserId == setup.FirstManager.Id).Items);
        Assert.Equal(retained.Id, recovery.WorkContext!.WorkItemId);
        Assert.Null(recovery.WorkContext.BoardId); // A native task is not a standard project board.
        var auth = new ScopedActionAuthorizationService(db);
        var cases = new WorkIssueService(db, auth, TimeProvider.System);
        var overview = new OverviewIssueReader(db, service, new(db, service, TimeProvider.System, new(db, service, auth, cases)),
            new ExecutiveDecisionService(db, new ChatTurnService(db)), new(db, new(db, TimeProvider.System), TimeProvider.System), cases);
        var cards = await overview.ReadAsync(setup.Organization.Id, actor, false, default);
        Assert.Empty(cards.UserActions);
        var blocker = Assert.Single(cards.Team);
        Assert.Equal("work:" + retained.Id.ToString("N"), blocker.Key);
        Assert.Equal(new[] { retained.Id, recovery.Id }.Order(), blocker.AffectedWork.Select(w => w.ItemId).Order());
        Assert.False(Assert.Single(blocker.Work).CanRetry);
        await ManagerWorkRecovery.HandleAsync(manager, recovery, context, default);
        Assert.NotNull(await ManagerWorkRecovery.HandleAsync(manager, recovery, context, default));
        Assert.Equal(1, manager.Assessments);
        directory = await service.ListAsync(setup.Organization.Id, actor);
        var source = directory.Boards.Single(b => b.OwnerOrganizationUserId == setup.Agent.Id).Items;
        Assert.Equal(Wire.PersonalTodoStatuses.Ready, source.Single(w => w.Id == retained.Id).Status);
        Assert.Equal(Wire.PersonalTodoStatuses.Ready, source.Single(w => w.Id == independent.Id).Status);
        Assert.Equal(3, await db.CoreWorkTasks.CountAsync());
    }

    private sealed class NativeRecoveryManager : CSweetManagerAgentBase
    {
        public int Assessments;
        public override string AgentId => "example.manager";
        public override string Version => "1.0.0";
        protected override string ManagementResponsibility => "Assigned project delivery";
        public override Task<WorkRecoveryAssessment> AssessWorkRecoveryAsync(ManagerWorkRecoveryContext recovery, AgentRuntimeContext context, CancellationToken token)
        {
            Assessments++;
            return Task.FromResult(new WorkRecoveryAssessment("Retry", "Reviewed the retained failure and the published permission correction."));
        }
    }
}
