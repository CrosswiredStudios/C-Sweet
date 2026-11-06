using CSweet.AgentHost.Broker;
using CSweet.Agent.SDK;
using CSweet.Application.Agents;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;

namespace CSweet.UnitTests;

public sealed class AgentGovernanceRegressionTests
{
    [Theory]
    [InlineData("999")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("Manual, Auto")]
    public void PostureMustBeANamedDefinedValue(string value) => Assert.False(AgentAuthority.TryParse(value, out _));

    [Fact]
    public async Task PostureIsSharedAcrossIndependentHostContextsAndAuditedAtomically()
    {
        await using var f = await Fixture.CreateAsync();
        var service = new AgentPostureService(new DatabaseAgentPostureStore(f.Db), new PermissivePostureAuthorization(), TimeProvider.System);
        await service.SetAsync(f.Installation, AgentPosture.Plan, f.Owner);
        await using var host = new CSweetDbContext(f.Options);
        var read = await new DatabaseAgentPostureStore(host).FindAsync(f.Installation);
        Assert.Equal(AgentPosture.Plan, read!.Posture);
        Assert.Contains(await host.AuditOutbox.ToListAsync(), x => x.SourceEntityId == f.Installation);
        Assert.NotEmpty(await host.ApplicationRealtimeOutbox.ToListAsync());
        Assert.Equal(1, (await host.AgentInstallations.SingleAsync()).GovernanceRevision);
    }

    [Fact]
    public async Task OnlyOwnerOrDirectManagerMayChangeAnOrganizationAgentsAuthority()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.True(await AgentGovernanceAccess.CanManageAsync(f.Db, f.Owner, f.Installation));
        var manager = Guid.NewGuid();
        var member = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, ApplicationUserId = manager,
            PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true };
        f.Db.CoreOrganizationUsers.Add(member);
        var employee = await f.Db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.Installation);
        employee.ReportsToOrganizationUserId = member.Id;
        await f.Db.SaveChangesAsync();
        Assert.True(await AgentGovernanceAccess.CanManageAsync(f.Db, manager, f.Installation));
        member.PermissionLevel = OrganizationPermissionLevel.Contributor;
        await f.Db.SaveChangesAsync();
        Assert.False(await AgentGovernanceAccess.CanManageAsync(f.Db, manager, f.Installation));
        Assert.False(await AgentGovernanceAccess.CanManageAsync(f.Db, Guid.NewGuid(), f.Installation));
    }

    [Fact]
    public async Task ManualQueuesExactRequestThenExecutesOnceAndReplaysReceipt()
    {
        await using var f = await Fixture.CreateAsync();
        var handler = new CountingHandler();
        var dispatcher = f.Dispatcher(handler);
        var pending = Assert.Single(await Invoke(dispatcher, f.Session));
        Assert.False(pending.Succeeded); Assert.Equal(0, handler.Calls);
        var approval = await f.Db.AgentCapabilityApprovals.SingleAsync();
        Assert.Equal("{}", approval.PayloadJson);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Approvals.DecideAsync(f.Org, approval.Id, Guid.NewGuid(), false, true));
        await f.Approvals.DecideAsync(f.Org, approval.Id, f.Owner, false, true);
        Assert.True(Assert.Single(await Invoke(dispatcher, f.Session)).Succeeded);
        Assert.True(Assert.Single(await Invoke(dispatcher, f.Session)).Succeeded);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("Succeeded", approval.Status);
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToListAsync(), x => x.TargetInstallationId == f.Installation);
    }

    [Fact]
    public async Task PlanRefusesTrackingAcknowledgementBecauseItWritesAReceipt()
    {
        Assert.Equal(CapabilityRiskClass.Write, CapabilityRiskCatalog.Classify("work.tracking.processed.v1"));
        await using var f = await Fixture.CreateAsync();
        await new AgentPostureService(new DatabaseAgentPostureStore(f.Db), new PermissivePostureAuthorization(), TimeProvider.System).SetAsync(f.Installation, AgentPosture.Plan, f.Owner);
        var handler = new CountingHandler();
        var result = Assert.Single(await Invoke(f.Dispatcher(handler), f.Session, "work.tracking.processed.v1"));
        Assert.False(result.Succeeded); Assert.Equal(0, handler.Calls);
        Assert.Empty(await f.Db.AgentCapabilityApprovals.ToListAsync());
    }

    [Fact]
    public async Task ChangingPostureInvalidatesPendingApprovals()
    {
        await using var f = await Fixture.CreateAsync();
        await Invoke(f.Dispatcher(new CountingHandler()), f.Session);
        var approval = await f.Db.AgentCapabilityApprovals.SingleAsync();
        await new AgentPostureService(new DatabaseAgentPostureStore(f.Db), new PermissivePostureAuthorization(), TimeProvider.System).SetAsync(f.Installation, AgentPosture.Plan, f.Owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Approvals.DecideAsync(f.Org, approval.Id, f.Owner, false, true));
    }

    [Fact]
    public async Task MissingHandlerDoesNotCreateAnUnexecutableApproval()
    {
        await using var f = await Fixture.CreateAsync();
        var dispatcher = new PlatformCapabilityDispatcher([], postures: new AgentPostureService(new DatabaseAgentPostureStore(f.Db), new PermissivePostureAuthorization(), TimeProvider.System), approvals: f.Approvals, temporalPolicy: new PermissiveTemporalPolicy());
        Assert.False(Assert.Single(await Invoke(dispatcher, f.Session)).Succeeded);
        Assert.Empty(await f.Db.AgentCapabilityApprovals.ToListAsync());
    }

    [Fact]
    public async Task ProviderRoutedCapabilityNeedsNoPlatformHandlerButStillPassesTheGate()
    {
        await using var f = await Fixture.CreateAsync();
        var request = new RequestCapability { RequestId = "routed", Capability = "work.item.create.v1", Payload = JsonPayload.FromUtf8("{}") };
        var auto = new PlatformCapabilityDispatcher([], new AutoPostureReader(), new UnusedApprovalService(), new PermissiveTemporalPolicy());
        // Remote MCP, connector and agent-provided tools have no platform handler; the gateway routes them.
        Assert.True((await auto.PreflightAsync(f.Session, request, default)).IsTerminal);
        Assert.False((await auto.PreflightAsync(f.Session, request, default, new CapabilityRoute(CapabilityRiskClass.Read))).IsTerminal);

        // Routing never bypasses posture: Plan still refuses a routed write.
        var postures = new AgentPostureService(new DatabaseAgentPostureStore(f.Db), new PermissivePostureAuthorization(), TimeProvider.System);
        await postures.SetAsync(f.Installation, AgentPosture.Plan, f.Owner);
        var plan = new PlatformCapabilityDispatcher([], postures, f.Approvals, new PermissiveTemporalPolicy());
        Assert.True((await plan.PreflightAsync(f.Session, request, default, new CapabilityRoute(CapabilityRiskClass.Write))).IsTerminal);
        Assert.False((await plan.PreflightAsync(f.Session, request, default, new CapabilityRoute(CapabilityRiskClass.Read))).IsTerminal);
    }

    [Fact]
    public async Task PolicyDeferralKeepsApprovalRetryableWithoutExecuting()
    {
        await using var f = await Fixture.CreateAsync();
        var admission = await f.Approvals.AdmitAsync(f.Org, f.Installation, "work.item.create.v1", [], 1, CapabilityRiskClass.Write);
        await f.Approvals.DecideAsync(f.Org, admission.ApprovalId, f.Owner, false, true);
        Assert.Equal("Execute", (await f.Approvals.AdmitAsync(f.Org, f.Installation, "work.item.create.v1", [], 1, CapabilityRiskClass.Write)).Status);
        await f.Approvals.DeferAsync(admission.ApprovalId);
        Assert.Equal("Execute", (await f.Approvals.AdmitAsync(f.Org, f.Installation, "work.item.create.v1", [], 1, CapabilityRiskClass.Write)).Status);
    }

    [Fact]
    public async Task ExecutionClaimAndDeferralNotifyInTheSameTransaction()
    {
        await using var f = await Fixture.CreateAsync();
        var admission = await f.Approvals.AdmitAsync(f.Org, f.Installation, "work.item.create.v1", [], 1, CapabilityRiskClass.Write);
        await f.Approvals.DecideAsync(f.Org, admission.ApprovalId, f.Owner, false, true);
        await f.Approvals.AdmitAsync(f.Org, f.Installation, "work.item.create.v1", [], 1, CapabilityRiskClass.Write);
        await f.Approvals.DeferAsync(admission.ApprovalId);
        var states = await f.Db.ApplicationRealtimeOutbox.Where(x => x.Subject == admission.ApprovalId.ToString("D"))
            .Select(x => x.DataJson).ToListAsync();
        // Pending (created) → Approved (decided) → Executing (claimed) → Approved (deferred): every transition is announced.
        Assert.Contains(states, x => x.Contains("Executing"));
        Assert.Equal(2, states.Count(x => x.Contains("Approved")));
    }

    [Fact]
    public async Task GrantChangeInvalidatesAnOldApprovalBeforeDecision()
    {
        await using var f = await Fixture.CreateAsync();
        await Invoke(f.Dispatcher(new CountingHandler()), f.Session);
        var approval = await f.Db.AgentCapabilityApprovals.SingleAsync();
        (await f.Db.AgentInstallationGrants.SingleAsync()).GrantRevision++;
        await f.Db.SaveChangesAsync();
        Assert.Equal("Superseded", approval.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Approvals.DecideAsync(f.Org, approval.Id, f.Owner, false, true));
        var admission = await f.Approvals.AdmitAsync(f.Org, f.Installation, approval.Capability, [], 1, CapabilityRiskClass.Write);
        Assert.Equal("GrantChanged", admission.Status);
    }

    [Fact]
    public async Task RecoveryReadCannotRevealAnotherInstallationsRequest()
    {
        await using var f = await Fixture.CreateAsync();
        await Invoke(f.Dispatcher(new CountingHandler()), f.Session);
        var reader = new AgentPostureService(new DatabaseAgentPostureStore(f.Db), new PermissivePostureAuthorization(), TimeProvider.System);
        var dispatcher = new PlatformCapabilityDispatcher([new AgentGovernanceCapabilityHandler(f.Db, reader)], postures: reader, approvals: f.Approvals, temporalPolicy: new PermissiveTemporalPolicy());
        var result = Assert.Single(await Invoke(dispatcher, f.Session, AgentGovernanceCapabilityHandler.Read));
        Assert.True(result.Succeeded);
        using var document = System.Text.Json.JsonDocument.Parse(result.Payload.Span.ToArray());
        Assert.Single(document.RootElement.GetProperty("approvals").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, document.RootElement.GetProperty("approvals")[0].GetProperty("payloadJson").ValueKind);
        var invalid = Assert.Single(await Invoke(dispatcher, f.Session with { BusinessId = Guid.NewGuid().ToString("D") }, AgentGovernanceCapabilityHandler.Read));
        Assert.False(invalid.Succeeded);
    }

    private static async Task<List<CapabilityResult>> Invoke(PlatformCapabilityDispatcher dispatcher, AgentSession session, string capability = "work.item.create.v1")
    {
        var results = new List<CapabilityResult>();
        await foreach (var result in dispatcher.InvokeAsync(session, new RequestCapability { RequestId = "request", Capability = capability, Payload = JsonPayload.FromUtf8("{}") }, default)) results.Add(result);
        return results;
    }
    private sealed class CountingHandler : IPlatformCapabilityHandler
    {
        public int Calls;
        public bool CanHandle(string capability) => true;
        public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request, [EnumeratorCancellation] CancellationToken token)
        { Calls++; yield return new() { RequestId = request.RequestId, Succeeded = true, Payload = JsonPayload.FromUtf8("{\"ok\":true}") }; await Task.CompletedTask; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Guid Org = Guid.NewGuid(), Installation = Guid.NewGuid(), Owner = Guid.NewGuid();
        public DbContextOptions<CSweetDbContext> Options = new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public CSweetDbContext Db = null!;
        public AgentCapabilityApprovalService Approvals = null!;
        public AgentSession Session => new("session", "test", Installation.ToString("D"), Org.ToString("D"), "runtime", "tick", new(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "work.item.create.v1", "work.tracking.processed.v1" }, 1));
        public PlatformCapabilityDispatcher Dispatcher(IPlatformCapabilityHandler handler) => new([handler], postures: new AgentPostureService(new DatabaseAgentPostureStore(Db), new PermissivePostureAuthorization(), TimeProvider.System), approvals: Approvals, temporalPolicy: new PermissiveTemporalPolicy());
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); f.Db = new(f.Options);
            f.Db.AgentInstallations.Add(new() { Id = f.Installation, BusinessId = f.Org.ToString("D") });
            f.Db.AgentInstallationGrants.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = f.Installation, GrantRevision = 1 });
            f.Db.CoreOrganizationUsers.AddRange(new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, ApplicationUserId = f.Owner, PermissionLevel = OrganizationPermissionLevel.Owner },
                new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Org, AgentInstallationId = f.Installation, EmployeeType = EmployeeType.Agent });
            await f.Db.SaveChangesAsync(); f.Approvals = new(f.Db, TimeProvider.System); return f;
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
