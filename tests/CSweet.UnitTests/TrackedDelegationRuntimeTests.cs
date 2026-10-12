using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class TrackedDelegationRuntimeTests
{
    [Fact]
    public async Task SharedSdkAndPlatformCarryStaffingRequestThroughAcknowledgementProgressBlockerAndFinalReport()
    {
        var clock = new Clock();
        await using var f = await TrackedDelegationTests.Fixture.Create(time: clock);
        var existing = await new PersonalTodoService(f.Db, TimeProvider.System).AddAsync(f.Org, new(f.Gabriel.Id, f.Gabriel.AgentInstallationId),
            new("Retry staffing", null, "Medium", null, "staffing-recovery"));
        var naomi = new JourneyAgent(true, existing.Id);
        var gabriel = new JourneyAgent(false, existing.Id);
        var coordinator = Runtime(f, f.Naomi.Id); var recipient = Runtime(f, f.Gabriel.Id);
        coordinator.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead,
            (_, _) => Task.FromResult(new CommunicationMessages([new(f.Source.Id, 1, f.Source.ConversationId, f.Matt.Id, "Matt", "Human", f.Source.Content, clock.Now)])));
        coordinator.RegisterCapability<JsonElement, OrganizationSnapshotResponse>(PlatformCapabilities.OrganizationSnapshotRead,
            (_, _) => Task.FromResult(new OrganizationSnapshotResponse(f.Org, "Active", [new(f.Gabriel.Id, f.Gabriel.DisplayName, "Agent", null,
                f.Naomi.Id, f.Gabriel.AgentInstallationId, true)], [], [], [], [], clock.Now)));
        await coordinator.DeliverEventAsync(naomi, CommunicationEvents.MessageReceived,
            new CommunicationMessageReceivedEvent(Guid.NewGuid(), f.Source.ConversationId.ToString("D"), f.Matt.Id.ToString("D"), f.Source.Content, null,
                Guid.NewGuid(), 1, f.Source.Id), identity: Identity(f.Naomi));
        var d = (await f.Service.ReadAsync(f.Org, f.Naomi.Id, new())).Single();
        Assert.Contains(coordinator.Progress, x => x.TryGetProperty("delta", out var value) && value.GetString()!.Contains("keep you updated"));
        await recipient.DeliverEventAsync(gabriel, DelegationCapabilities.Received, new DelegationWake(d.Id), identity: Identity(f.Gabriel));
        d = (await f.Service.ReadAsync(f.Org, f.Naomi.Id, new(d.Id))).Single();
        Assert.Equal(existing.Id, d.WorkItemId); Assert.Equal(2, await f.Db.CoreWorkTasks.CountAsync());
        await coordinator.DeliverEventAsync(naomi, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Naomi));
        var ack = await f.Db.CoreConversationMessages.SingleAsync(x => x.ConversationId == f.Source.ConversationId && x.Content.StartsWith("I will retry"));
        Assert.Contains("[Track work]", ack.Content);
        await recipient.DeliverEventAsync(gabriel, CommunicationEvents.MessageReceived,
            new CommunicationMessageReceivedEvent(Guid.NewGuid(), d.RecipientConversationId.ToString("D"), f.Naomi.Id.ToString("D"), d.Instruction, null,
                d.ExecutionTurnId!.Value, 1, d.ExecutionMessageId!.Value), identity: Identity(f.Gabriel));
        d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single(); Assert.True(d.ExecutionHandled);
        clock.Now += TimeSpan.FromMinutes(5); await f.Service.ReconcileAsync();
        await coordinator.DeliverEventAsync(naomi, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Naomi));
        Assert.Contains(await f.Db.CoreConversationMessages.ToListAsync(), x => x.ConversationId == f.Source.ConversationId && x.Content.Contains("gateway"));
        d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
        await recipient.CreateContext().Platform.Delegation.RespondAsync(new(d.Id, d.Revision, "Blocked", "Staffing approval is needed.", "blocker"));
        await coordinator.DeliverEventAsync(naomi, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Naomi));
        Assert.Contains(await f.Db.CoreConversationMessages.ToListAsync(), x => x.ConversationId == f.Source.ConversationId && x.Content.Contains("approval is needed"));
        var item = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == existing.Id); item.Status = WorkTaskStatus.Completed; item.Revision++;
        await f.Db.SaveChangesAsync(); await f.Service.ReconcileAsync(); gabriel.Finished = true;
        await recipient.DeliverEventAsync(gabriel, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Gabriel));
        await coordinator.DeliverEventAsync(naomi, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Naomi));
        Assert.Equal("Completed", (await f.Service.ReadAsync(f.Org, f.Naomi.Id, new(d.Id))).Single().Status);
        var count = await f.Db.CoreConversationMessages.CountAsync();
        await recipient.DeliverEventAsync(gabriel, DelegationCapabilities.Received, new DelegationWake(d.Id), identity: Identity(f.Gabriel));
        await coordinator.DeliverEventAsync(naomi, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Naomi));
        Assert.Equal(count, await f.Db.CoreConversationMessages.CountAsync());
        Assert.Single(await f.Db.ChatTurns.ToListAsync());
        Assert.All(await f.Db.CoreConversationMessages.Where(x => x.IdempotencyKey != null && x.IdempotencyKey.StartsWith("delegation-report:")).ToListAsync(),
            x => Assert.Equal(CommunicationDeliveryIntent.Inform, x.DeliveryIntent));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingTrackingBeforeAcknowledgementOrExecutionPreservesExecutionAndSuppressesReports(bool beforeAcknowledgement)
    {
        var clock = new Clock(); await using var f = await TrackedDelegationTests.Fixture.Create(time: clock);
        var existing = await new PersonalTodoService(f.Db, TimeProvider.System).AddAsync(f.Org, new(f.Gabriel.Id, f.Gabriel.AgentInstallationId),
            new("Retry staffing", null, "Medium", null, "cancel-tracking-work"));
        var gabriel = new JourneyAgent(false, existing.Id); var naomi = new JourneyAgent(true, existing.Id);
        var recipient = Runtime(f, f.Gabriel.Id); var coordinator = Runtime(f, f.Naomi.Id);
        var d = await f.Service.CreateAsync(f.Org, f.Naomi.Id, f.Request());
        if (!beforeAcknowledgement)
            await recipient.DeliverEventAsync(gabriel, DelegationCapabilities.Received, new DelegationWake(d.Id), identity: Identity(f.Gabriel));
        d = (await f.Service.ReadAsync(f.Org, f.Naomi.Id, new(d.Id))).Single();
        d = await f.Service.CancelAsync(f.Org, f.Naomi.Id, new(d.Id, d.Revision, "stop-tracking"));
        Assert.True(d.TrackingCancelled); Assert.Equal("Cancelled", d.Status);
        var sourceMessages = await f.Db.CoreConversationMessages.CountAsync(x => x.ConversationId == f.Source.ConversationId);
        Assert.Empty(await f.Service.ReadAsync(f.Org, f.Naomi.Id, new()));
        Assert.Single(await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new()));
        await recipient.DeliverEventAsync(gabriel, DelegationCapabilities.Received, new DelegationWake(d.Id), identity: Identity(f.Gabriel));
        d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
        Assert.Equal("Active", d.ExecutionStatus); Assert.NotNull(d.ExecutionTurnId);
        var prompt = await new CSweet.Infrastructure.Core.MemoryRecallDispatchEvidence(f.Db).CapturePromptAsync(
            new(d.ExecutionTurnId!.Value, 1, f.Org, d.RecipientConversationId, d.ExecutionMessageId!.Value,
                f.Gabriel.Id, f.Naomi.Id, f.Gabriel.AgentInstallationId!.Value, new string('A', 64)), null, default);
        Assert.Equal(d.Id.ToString("D"), prompt.Metadata.Context["delegationId"]);
        var incoming = new CommunicationMessageReceivedEvent(Guid.NewGuid(), d.RecipientConversationId.ToString("D"),
            f.Naomi.Id.ToString("D"), d.Instruction, null, d.ExecutionTurnId!.Value, 1, d.ExecutionMessageId!.Value);
        await recipient.DeliverEventAsync(gabriel, CommunicationEvents.MessageReceived, incoming, identity: Identity(f.Gabriel));
        await recipient.DeliverEventAsync(gabriel, CommunicationEvents.MessageReceived, incoming, identity: Identity(f.Gabriel));
        Assert.Equal(1, gabriel.ExecutionCalls);
        d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single(); Assert.True(d.ExecutionHandled);
        Assert.Equal(existing.Id, d.WorkItemId);
        Assert.All(await f.Db.WorkTrackingSubscriptions.Where(x => x.DelegationId == d.Id).ToListAsync(), x => Assert.NotNull(x.UnfollowedAt));
        clock.Now += TimeSpan.FromHours(25); await f.Service.ReconcileAsync();
        await coordinator.DeliverEventAsync(naomi, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Naomi));
        Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        var work = await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == existing.Id); Assert.NotEqual(WorkTaskStatus.Cancelled, work.Status);
        work.Status = WorkTaskStatus.Completed; work.Revision++; await f.Db.SaveChangesAsync(); await f.Service.ReconcileAsync();
        gabriel.Finished = true;
        await recipient.DeliverEventAsync(gabriel, DelegationCapabilities.Changed, new WorkTrackingWake(existing.Id, d.Id), identity: Identity(f.Gabriel));
        d = (await f.Service.ReadAsync(f.Org, f.Gabriel.Id, new(d.Id))).Single();
        Assert.Equal("Completed", d.ExecutionStatus); Assert.Equal("Cancelled", d.Status);
        Assert.All(d.Updates, x => Assert.False(x.Reported));
        Assert.Empty(await f.Service.PendingAsync(f.Org, f.Naomi.Id, 20));
        Assert.Equal(sourceMessages, await f.Db.CoreConversationMessages.CountAsync(x => x.ConversationId == f.Source.ConversationId));
        Assert.Single(await f.Db.ChatTurns.ToListAsync());
        Assert.Equal(WorkTaskStatus.Cancelled, (await f.Db.CoreWorkTasks.SingleAsync(x => x.Id == d.CommitmentItemId)).Status);
    }

    private static AgentIdentity Identity(OrganizationUser user) => new(user.Id.ToString("D"), user.DisplayName, null, null, null, [], null, null, null);
    private static AgentTestRuntime Runtime(TrackedDelegationTests.Fixture f, Guid actor) => new AgentTestRuntime()
        .RegisterCapability<DelegateWithTrackingRequest, DelegationSnapshot>(DelegationCapabilities.Create, (r, t) => f.Service.CreateAsync(f.Org, actor, r, t))
        .RegisterCapability<ReadDelegationRequest, IReadOnlyList<DelegationSnapshot>>(DelegationCapabilities.Read, (r, t) => f.Service.ReadAsync(f.Org, actor, r, t))
        .RegisterCapability<DelegationResponseRequest, DelegationSnapshot>(DelegationCapabilities.Respond, (r, t) => f.Service.RespondAsync(f.Org, actor, r, t))
        .RegisterCapability<PendingTrackingRequest, IReadOnlyList<PendingTrackingUpdate>>(DelegationCapabilities.Pending, (r, t) => f.Service.PendingAsync(f.Org, actor, r.Limit, t))
        .RegisterCapability<PublishDelegationReportRequest, DelegationSnapshot>(DelegationCapabilities.Publish, (r, t) => f.Service.PublishAsync(f.Org, actor, r, t));
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class JourneyAgent(bool coordinator, Guid work) : CSweetAgentBase
    {
        public override string AgentId => "test.staffing-journey"; public override string Version => "1.0.0";
        public bool Finished; public int ExecutionCalls;
        public override Task<DelegationIntent> ClassifyDelegationIntentAsync(CommunicationMessageReceivedEvent incoming, AgentRuntimeContext context, CancellationToken token) =>
            Task.FromResult(coordinator ? new DelegationIntent("Delegate", "Gabriel Reyes", "Retry staffing: gateway and recovery fixes installed", "Resolve staffing readiness after the gateway and recovery fixes.", null) : new("Other", null, null, null, null));
        public override Task<DelegationResponse> HandleDelegationReceivedAsync(DelegationSnapshot d, AgentRuntimeContext context, CancellationToken token) =>
            Task.FromResult<DelegationResponse>(new DelegationResponse.Acceptance("I will retry staffing using the existing recovery work item.", work));
        public override async Task HandleEventAsync(AgentEventEnvelope envelope, AgentRuntimeContext context, CancellationToken token)
        {
            ExecutionCalls++;
            var incoming = envelope.Data.Deserialize<CommunicationMessageReceivedEvent>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await using var stream = context.CreateTurnStream(incoming.ConversationId, incoming.TurnId, incoming.Attempt);
            await stream.CommitAsync("The gateway is ready; I am checking recovery and staffing.", token);
        }
        public override Task<Guid?> ResolveDelegationWorkAsync(DelegationSnapshot d, string response, AgentRuntimeContext context, CancellationToken token) => Task.FromResult<Guid?>(work);
        public override Task<DelegationResponse.FinalOutcome?> ReviewDelegationOutcomeAsync(DelegationSnapshot d, AgentRuntimeContext context, CancellationToken token) =>
            Task.FromResult<DelegationResponse.FinalOutcome?>(Finished ? new DelegationResponse.FinalOutcome("Staffing is ready after the gateway and recovery checks.", true) : null);
        public override Task<string> SummarizeDelegationUpdateAsync(DelegationSnapshot d, TrackingUpdate update, AgentRuntimeContext context, CancellationToken token) => Task.FromResult(update.Summary);
    }
}
