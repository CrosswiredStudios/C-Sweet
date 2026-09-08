using System.Text.Json;
using System.Text;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.UnitTests;

public sealed class ConnectorActionApprovalTests
{
    [Fact]
    public async Task ReviewedOwnerPolicyAuthorizesExactExecutionWithoutInventingAnIndividualDecision()
    {
        await using var f = await Fixture.Create();
        _ = await ApprovePolicy(f);
        var proposal = await f.Request();
        Assert.Equal(ProposalStatus.Approved, proposal.Status);
        var binding = ConnectorActionApprovalService.Parse(proposal);
        Assert.False(binding.AlwaysRequiresApproval); Assert.NotNull(binding.StandingPolicy);
        Assert.Contains("owner-approved standing policy", proposal.Summary);
        Assert.Empty(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorActionApprovalService.ReceiptKind).ToArrayAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default));
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] {
            ConnectorPlanServiceTests.Fixture.Capability, PlatformCapabilities.ConnectorActionRead });
        await f.Inner.Db.SaveChangesAsync();
        var action = await new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service)
            .ReadAsync(f.Inner.Organization, f.Inner.Requester.Id, new(proposal.Id), default);
        Assert.Equal("Approved", action.Status); Assert.Null(action.Decision);
        var transport = new MutationTransport();
        await f.Execute(f.Executor(transport));
        Assert.Equal("Completed", f.Plan.Status); Assert.Single(transport.Requests);
        Assert.Single(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorStandingPolicyService.ExecutionPermitKind).ToArrayAsync());
    }

    [Fact]
    public async Task StandingPolicyRevokedDuringOwnershipReadStopsBeforeTheMutation()
    {
        await using var f = await Fixture.Create(ownership: true);
        var (service, policy, userId) = await ApprovePolicy(f);
        _ = await f.Request();
        var transport = new MutationTransport { AfterOwnershipRead = () => service.RevokeAsync(f.Inner.Organization,
            f.Inner.Requester.Id, userId, f.Plan.Capability, policy.Id, policy.Revision, default) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Execute(f.Executor(transport)));
        Assert.Equal("GET", Assert.Single(transport.Requests));
        Assert.Equal("Blocked", f.Plan.Status); Assert.Null(f.Plan.ResultJson);
    }

    [Fact]
    public async Task PlanOutsideOwnerConstraintsStillRequiresTheAccountableCeo()
    {
        await using var f = await Fixture.Create();
        _ = await ApprovePolicy(f, permittedTitle: "A different permitted title");
        var proposal = await f.Request();
        var binding = ConnectorActionApprovalService.Parse(proposal);
        Assert.Null(binding.StandingPolicy); Assert.True(binding.AlwaysRequiresApproval);
        Assert.Equal(f.Manager.Id, binding.ApproverOrganizationUserId);
        Assert.Equal("AwaitingApproval", f.Plan.Status);
        var transport = new MutationTransport();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Execute(f.Executor(transport)));
        Assert.Empty(transport.Requests);
    }

    private static async Task<(ConnectorStandingPolicyService Service, ConnectorStandingPolicyView Policy, Guid UserId)> ApprovePolicy(
        Fixture f, string? permittedTitle = null)
    {
        var userId = Guid.NewGuid();
        f.Manager.ApplicationUserId = userId; f.Manager.PermissionLevel = OrganizationPermissionLevel.Owner;
        f.Manager.Role = new() { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, Name = "CEO" };
        f.Inner.Db.Add(f.Manager.Role);
        f.Inner.Db.AgentInstallationConfigurations.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = f.Inner.Requester.Id,
            SchemaVersion = "1", SettingsJson = "{\"approvalMode\":\"Fully Autonomous\"}" });
        await f.Inner.Db.SaveChangesAsync();
        var service = new ConnectorStandingPolicyService(f.Inner.Db, f.Inner.Service, new Audit());
        var review = await service.ReviewAsync(f.Inner.Organization, f.Inner.Requester.Id, userId, f.Plan.Id, f.Plan.PlanHash, default);
        var fields = review.MutableFields.Select(field => field.Source == "body" && permittedTitle is not null
            ? new ConnectorPolicyFieldRule(field, false, [JsonSerializer.SerializeToElement(permittedTitle)])
            : new ConnectorPolicyFieldRule(field, true, [])).ToArray();
        var now = DateTimeOffset.UtcNow;
        var definition = new ConnectorStandingPolicyDefinition(fields, [0, 1, 2, 3, 4, 5, 6], 0, 1440, 5,
            now.AddMinutes(-1), now.AddHours(2), []);
        var policy = await service.ApproveAsync(f.Inner.Organization, f.Inner.Requester.Id, userId,
            new(f.Plan.Id, f.Plan.PlanHash, review.ReviewHash, definition, null), default);
        return (service, policy, userId);
    }

    [Fact]
    public async Task HistoricalStandingPolicyCannotAuthorizeANewConnectorPlan()
    {
        await using var f = await Fixture.Create();
        f.Manager.Role = new() { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, Name = "CEO" };
        f.Inner.Db.Add(f.Manager.Role);
        f.Inner.Db.AgentInstallationConfigurations.Add(new()
        {
            Id = Guid.NewGuid(), AgentInstallationId = f.Inner.Requester.Id, SchemaVersion = "1",
            SettingsJson = "{\"approvalMode\":\"Fully Autonomous\"}"
        });
        f.Inner.Db.PluginStandingPolicies.Add(new()
        {
            Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, AgentInstallationId = f.Inner.Requester.Id,
            ChannelId = "confirmed", Revision = 1, Status = PluginStandingPolicyStatus.Approved,
            ApprovedByOrganizationUserId = f.Manager.Id, PayloadHash = f.Plan.PlanHash,
            PolicyJson = "{\"allowedActionCategories\":[\"Publishing\",\"ContentManagement\",\"CommentReplies\",\"Moderation\",\"LiveConfiguration\",\"Engagement\"]}"
        });
        await f.Inner.Db.SaveChangesAsync();
        var proposal = await f.Request();
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal("AwaitingApproval", f.Plan.Status);
        var binding = ConnectorActionApprovalService.Parse(proposal);
        Assert.True(binding.AlwaysRequiresApproval);
        Assert.Equal(f.Manager.Id, binding.ApproverOrganizationUserId);
        Assert.Equal("Fully Autonomous", binding.ApprovalMode);
        var transport = new MutationTransport();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(f.Executor(transport)));
        Assert.Empty(transport.Requests);
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        await f.Execute(f.Executor(transport));
        Assert.Equal("Completed", f.Plan.Status);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task WrongResponseOwnerAfterMutationIsIndeterminateAndNeverReleased()
    {
        await using var f = await Fixture.Create(responseBinding: true);
        var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Execute(f.Executor(new MutationTransport())));
        Assert.Equal("Indeterminate", f.Plan.Status);
        Assert.Null(f.Plan.ResultJson);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsPendingOrApprovedWorkAndPersistsOneReceipt(bool approved)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] {
            ConnectorPlanServiceTests.Fixture.Capability, PlatformCapabilities.ConnectorActionCancel });
        await f.Inner.Db.SaveChangesAsync();
        if (approved) await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        var request = new CancelConnectorAction(proposal.Id, "cancel-once");
        var result = await service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id, request, default);
        Assert.Equal("Cancelled", result.Status); Assert.Null(result.Result); Assert.Null(result.Decision);
        Assert.Equal("Cancelled", (await service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id, request, default)).Status);
        Assert.Single(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorActionService.CancellationKind).ToArrayAsync());
        var transport = new MutationTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(f.Executor(transport)));
        Assert.Empty(transport.Requests);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id,
            request with { IdempotencyKey = "different" }, default));
    }

    [Theory]
    [InlineData("Executing")]
    [InlineData("Completed")]
    [InlineData("Indeterminate")]
    public async Task CancellationCannotEraseStartedOrUncertainOutcomes(string status)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { PlatformCapabilities.ConnectorActionCancel });
        f.Plan.Status = status; await f.Inner.Db.SaveChangesAsync();
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id,
            new(proposal.Id, "cancel"), default));
        Assert.Equal(status, f.Plan.Status);
        Assert.Empty(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorActionService.CancellationKind).ToArrayAsync());
    }

    [Fact]
    public async Task CancellationNeedsItsOwnGrantAndCannotSelectAnotherTenantOrInstallation()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        var request = new CancelConnectorAction(proposal.Id, "cancel");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id, request, default));
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { PlatformCapabilities.ConnectorActionCancel });
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelAsync(Guid.NewGuid(), f.Inner.Requester.Id, request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelAsync(f.Inner.Organization, Guid.NewGuid(), request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id,
            request with { ActionId = Guid.NewGuid() }, default));
        Assert.Equal("AwaitingApproval", f.Plan.Status);
    }

    [Fact]
    public async Task CancellationCannotOverwriteACompetingExecutionClaim()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { PlatformCapabilities.ConnectorActionCancel });
        await f.Inner.Db.SaveChangesAsync();
        var options = (DbContextOptions<CSweetDbContext>)f.Inner.Db.GetService<IDbContextOptions>();
        await using (var competing = new CSweetDbContext(options))
        {
            var claimed = await competing.ConnectorExecutions.SingleAsync();
            claimed.Status = "Executing"; claimed.Revision++;
            await competing.SaveChangesAsync();
        }
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.CancelAsync(f.Inner.Organization, f.Inner.Requester.Id,
            new(proposal.Id, "cancel"), default));
        await using var verify = new CSweetDbContext(options);
        Assert.Equal("Executing", (await verify.ConnectorExecutions.SingleAsync()).Status);
    }

    [Fact]
    public async Task RevisionFeedbackIsExactAndDisappearsWhenResultAuthorityIsRevoked()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] {
            ConnectorPlanServiceTests.Fixture.Capability, PlatformCapabilities.ConnectorActionRead });
        await f.Inner.Db.SaveChangesAsync();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal) with
            { Decision = "RequestRevision", Comment = "Please shorten the reply." }, default);
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        var result = await service.ReadAsync(f.Inner.Organization, f.Inner.Requester.Id, new(proposal.Id), default);
        Assert.Equal("RequestRevision", result.Decision!.Decision); Assert.Equal("Please shorten the reply.", result.Decision.Comment);
        Assert.Null(result.Result);
        f.Inner.Connection.Status = PluginConnectionStatus.Revoked; await f.Inner.Db.SaveChangesAsync();
        result = await service.ReadAsync(f.Inner.Organization, f.Inner.Requester.Id, new(proposal.Id), default);
        Assert.Equal("Unavailable", result.Status); Assert.Null(result.Decision);
    }

    [Fact]
    public async Task ExactManagerDecisionIsDurableAndReplayCannotCreateAnotherAction()
    {
        await using var f = await Fixture.Create();
        var proposal = await f.Request();
        Assert.DoesNotContain(f.Plan.Capability, proposal.Summary);
        Assert.Equal(proposal.Id, (await f.Request()).Id);
        Assert.Equal("AwaitingApproval", f.Plan.Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.RequireApproved());
        Assert.Equal("Approved", await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default));
        Assert.Equal("Approved", await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default));
        Assert.Equal(f.Plan.IdempotencyKey, (await f.RequireApproved()).IdempotencyKey);
        Assert.Single(await f.Inner.Db.ActionProposals.ToArrayAsync());
        Assert.Single(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorActionApprovalService.ReceiptKind).ToArrayAsync());
        Assert.Equal(2, await f.Inner.Db.PluginOperationalStates.CountAsync(x => x.Kind == ConnectorActionApprovalService.EventKind));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("hash")]
    [InlineData("revision")]
    [InlineData("resource")]
    [InlineData("action-key")]
    [InlineData("preview")]
    public async Task TamperedOrUnauthorizedDecisionsNeverAuthorizeExecution(string change)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request(); var request = Decision(proposal);
        var actor = f.Manager.Id; var organization = f.Inner.Organization;
        switch (change)
        {
            case "actor": actor = Guid.NewGuid(); break;
            case "tenant": organization = Guid.NewGuid(); break;
            case "hash": request = request with { PayloadHash = new string('a', 64) }; break;
            case "revision": request = request with { ExpectedRevision = 999 }; break;
            case "resource": request = request with { ResourceId = "another-channel" }; break;
            case "action-key": request = request with { ActionIdempotencyKey = "different" }; break;
            case "preview": proposal.PayloadJson = JsonSerializer.Serialize(ConnectorActionApprovalService.Parse(proposal) with
                { ReviewPayload = JsonSerializer.SerializeToElement(new { title = "different" }) }, Json); await f.Inner.Db.SaveChangesAsync(); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.DecideAsync(organization, actor, request, default));
        Assert.Equal(ProposalStatus.Pending, proposal.Status);
        Assert.Equal("AwaitingApproval", f.Plan.Status);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("manager")]
    [InlineData("disconnect")]
    [InlineData("expiry")]
    public async Task ExecutionRechecksAuthorityAfterApproval(string change)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        switch (change)
        {
            case "grant": f.Inner.Requester.Grant!.GrantRevision++; break;
            case "manager": f.Employee.ReportsToOrganizationUserId = Guid.NewGuid(); break;
            case "disconnect": f.Inner.Connection.Status = PluginConnectionStatus.Revoked; break;
            case "expiry": f.Plan.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); break;
        }
        await f.Inner.Db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => f.RequireApproved());
    }

    [Theory]
    [InlineData("Reject", "Rejected")]
    [InlineData("RequestRevision", "RevisionRequested")]
    public async Task NegativeDecisionIsTerminalAndCannotBeReplayedAsApproval(string decision, string expected)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        Assert.Equal(expected, await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id,
            Decision(proposal) with { Decision = decision, Comment = "Please adjust the draft." }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.RequireApproved());
    }

    [Fact]
    public async Task MissingManagerFallsBackToExactlyOneActiveCeo()
    {
        await using var f = await Fixture.Create();
        f.Employee.ReportsToOrganizationUserId = null;
        f.Manager.Role = new() { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, Name = "CEO" };
        f.Inner.Db.Add(f.Manager.Role);
        await f.Inner.Db.SaveChangesAsync();
        var proposal = await f.Request();
        Assert.Equal(f.Manager.Id, ConnectorActionApprovalService.Parse(proposal).ApproverOrganizationUserId);
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        _ = await f.RequireApproved();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task HumanReviewUsesOneProtectedConversationAndOneAuthoritativeCard()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        var service = new ConnectorApprovalConversationService(f.Inner.Db);
        await service.EnsureAsync(proposal, ConnectorActionApprovalService.Parse(proposal), default);
        await service.EnsureAsync(proposal, ConnectorActionApprovalService.Parse(proposal), default);
        var chat = Assert.Single(await f.Inner.Db.CoreConversations.Include(x => x.Participants).ToArrayAsync());
        Assert.True(chat.IsPrivate); Assert.True(chat.IsDeletionProtected); Assert.Equal(2, chat.Participants.Count);
        var messages = await f.Inner.Db.CoreConversationMessages.ToArrayAsync(); Assert.Single(messages);
        Assert.Single(await f.Inner.Db.UserNotifications.ToArrayAsync());
        var cards = await service.ReadCardsAsync(f.Inner.Organization, chat.Id, f.Manager.Id, messages, default);
        Assert.True(Assert.Single(cards).Value.CanDecide);
        var hub = new CommunicationHubService(f.Inner.Db, new Audit(), new ChatTurnService(f.Inner.Db));
        var visible = await hub.ListMessagesAsync(f.Inner.Organization, chat.Id, f.Manager.Id);
        Assert.Equal(proposal.Id, Assert.Single(visible!).ConnectorApproval!.Action.ProposalId);
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id,
            Decision(proposal) with { Decision = "RequestRevision", Comment = "Please change the title." }, default);
        cards = await service.ReadCardsAsync(f.Inner.Organization, chat.Id, f.Manager.Id, messages, default);
        var revised = Assert.Single(cards).Value;
        Assert.False(revised.CanDecide); Assert.Equal("RevisionRequested", revised.ExecutionStatus);
        Assert.Equal("Please change the title.", revised.DecisionComment);
    }

    [Fact]
    public async Task CopiedCorrelationCannotProjectAnApprovalIntoAnotherMessageOrChat()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        var service = new ConnectorApprovalConversationService(f.Inner.Db);
        await service.EnsureAsync(proposal, ConnectorActionApprovalService.Parse(proposal), default);
        var message = await f.Inner.Db.CoreConversationMessages.SingleAsync();
        var forged = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = message.ConversationId,
            CorrelationId = proposal.Id, SourceProvider = ConnectorApprovalConversationService.MessageSource };
        Assert.Empty(await service.ReadCardsAsync(f.Inner.Organization, message.ConversationId, f.Manager.Id, [forged], default));
        Assert.Empty(await service.ReadCardsAsync(Guid.NewGuid(), message.ConversationId, f.Manager.Id, [message], default));
        Assert.Empty(await service.ReadCardsAsync(f.Inner.Organization, Guid.NewGuid(), f.Manager.Id, [message], default));
        Assert.Empty(await service.ReadCardsAsync(f.Inner.Organization, message.ConversationId, Guid.NewGuid(), [message], default));
        var requesterView = await service.ReadCardsAsync(f.Inner.Organization, message.ConversationId, f.Employee.Id, [message], default);
        Assert.False(Assert.Single(requesterView).Value.CanDecide);
    }

    [Fact]
    public async Task DeclaringAnEventWithoutItsGrantDoesNotDeliverWork()
    {
        await using var f = await Fixture.Create();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Inner.Requester.PackageVersion!.ManifestJson, Json)!;
        f.Inner.Requester.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest with
            { Events = new() { Subscribes = [ConnectorActionEvents.Changed] } }, Json);
        await f.Inner.Db.SaveChangesAsync(); _ = await f.Request();
        var dispatcher = new ConnectorActionEventDispatcher(f.Inner.Db,
            new AgentWorkInbox(f.Inner.Db, new EphemeralDataProtectionProvider(), TimeProvider.System));
        await dispatcher.DispatchAsync(default);
        Assert.Empty(await f.Inner.Db.AgentWorkItems.ToArrayAsync());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"actionId\":\"00000000-0000-0000-0000-000000000000\",\"installationId\":\"other\"}")]
    [InlineData("{\"actionId\":\"first\",\"actionId\":\"second\"}")]
    public async Task ActionControlRejectsMalformedOrCallerSelectedAuthority(string json)
    {
        await using var f = await Fixture.Create();
        var handler = new ConnectorActionCapabilityHandler(new(f.Inner.Db, f.Inner.Service, f.Service));
        var session = new AgentSession("session", "requester", f.Inner.Requester.Id.ToString("D"),
            f.Inner.Organization.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([PlatformCapabilities.ConnectorActionRead]), 1));
        var results = new List<CapabilityResult>();
        await foreach (var response in handler.HandleAsync(session, new RequestCapability { RequestId = "read",
            Capability = PlatformCapabilities.ConnectorActionRead, Payload = JsonPayload.FromUtf8(json) }, default)) results.Add(response);
        Assert.False(Assert.Single(results).Succeeded);
        Assert.Empty(await f.Inner.Db.ActionProposals.ToArrayAsync());
    }

    [Fact]
    public async Task ActionEventDeliveryTargetsTheExactSubscriberAndSurvivesCheckpointReplay()
    {
        await using var f = await Fixture.Create();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Inner.Requester.PackageVersion!.ManifestJson, Json)!;
        f.Inner.Requester.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest with
            { Events = new() { Subscribes = [ConnectorActionEvents.Changed] } }, Json);
        f.Inner.Requester.Grant!.EventSubscriptionsJson = JsonSerializer.Serialize(new[] { ConnectorActionEvents.Changed });
        await f.Inner.Db.SaveChangesAsync();
        var proposal = await f.Request();
        var dispatcher = new ConnectorActionEventDispatcher(f.Inner.Db,
            new AgentWorkInbox(f.Inner.Db, new EphemeralDataProtectionProvider(), TimeProvider.System));
        await dispatcher.DispatchAsync(default);
        var work = Assert.Single(await f.Inner.Db.AgentWorkItems.ToArrayAsync());
        Assert.Equal(f.Inner.Requester.Id, work.AgentInstallationId);
        Assert.Equal(ConnectorActionEvents.Changed, work.Name);
        Assert.Equal(proposal.Id.ToString("D"), work.CorrelationId);
        var notification = await f.Inner.Db.PluginOperationalStates.SingleAsync(x => x.Kind == ConnectorActionEventDispatcher.DeliveredKind);
        Assert.Equal(notification.Id.ToString("D"), work.SourceId);
        notification.Kind = ConnectorActionApprovalService.EventKind;
        await f.Inner.Db.SaveChangesAsync(); // Simulate a crash after inbox commit but before outbox checkpoint.
        await dispatcher.DispatchAsync(default);
        Assert.Single(await f.Inner.Db.AgentWorkItems.ToArrayAsync());
    }

    [Fact]
    public async Task DurableDispatcherExecutesAnApprovedActionOnlyOnce()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        var transport = new MutationTransport();
        var dispatcher = new ConnectorActionDispatchService(f.Inner.Db, f.Executor(transport), f.Service);
        Assert.True(await dispatcher.ProcessNextAsync(default));
        Assert.False(await dispatcher.ProcessNextAsync(default));
        Assert.Equal("Completed", f.Plan.Status); Assert.Single(transport.Requests);
        Assert.Contains(await f.Inner.Db.PluginOperationalStates.ToArrayAsync(), x =>
            x.Kind == ConnectorActionApprovalService.EventKind && x.ExternalKey == $"{proposal.Id:N}:Completed");
    }

    [Theory]
    [InlineData("Executing", "Indeterminate")]
    [InlineData("Approved", "Blocked")]
    public async Task DispatcherRecoversStaleSendOrExpiredApprovalWithoutAnExternalRequest(string initial, string expected)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        f.Plan.Status = initial; f.Plan.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        if (initial == "Approved") f.Plan.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await f.Inner.Db.SaveChangesAsync();
        var transport = new MutationTransport();
        var dispatcher = new ConnectorActionDispatchService(f.Inner.Db, f.Executor(transport), f.Service);
        Assert.True(await dispatcher.ProcessNextAsync(default));
        Assert.Equal(expected, f.Plan.Status); Assert.Empty(transport.Requests);
        Assert.False(await dispatcher.ProcessNextAsync(default));
    }

    [Fact]
    public async Task TypedActionRequestIsIdempotentAndReadRequiresExactCallerAndCurrentAuthority()
    {
        await using var f = await Fixture.Create();
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] {
            ConnectorPlanServiceTests.Fixture.Capability, PlatformCapabilities.ConnectorActionRequest, PlatformCapabilities.ConnectorActionRead });
        await f.Inner.Db.SaveChangesAsync();
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        var request = new RequestConnectorAction(ConnectorPlanServiceTests.Fixture.Capability,
            ConnectorPlanServiceTests.Fixture.Input("Approved exact title"), "stable");
        var action = await service.RequestAsync(f.Inner.Organization, f.Inner.Requester.Id, request, default);
        Assert.Equal(action.ActionId, (await service.RequestAsync(f.Inner.Organization, f.Inner.Requester.Id, request, default)).ActionId);
        Assert.Equal("AwaitingApproval", action.Status);
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(await f.Request()), default);
        await f.Execute(f.Executor(new MutationTransport()));
        var result = await service.ReadAsync(f.Inner.Organization, f.Inner.Requester.Id, new(action.ActionId), default);
        Assert.Equal("Completed", result.Status); Assert.Equal("value", result.Result!.Value.GetProperty("data").GetString());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(f.Inner.Organization, Guid.NewGuid(), new(action.ActionId), default));
        f.Inner.Connection.Status = PluginConnectionStatus.Revoked; await f.Inner.Db.SaveChangesAsync();
        result = await service.ReadAsync(f.Inner.Organization, f.Inner.Requester.Id, new(action.ActionId), default);
        Assert.Equal("Unavailable", result.Status); Assert.Null(result.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActionControlsAndProviderOperationNeedIndependentGrants(bool controlGranted)
    {
        await using var f = await Fixture.Create();
        if (controlGranted) f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { PlatformCapabilities.ConnectorActionRequest });
        await f.Inner.Db.SaveChangesAsync();
        var service = new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RequestAsync(f.Inner.Organization, f.Inner.Requester.Id,
            new(ConnectorPlanServiceTests.Fixture.Capability, ConnectorPlanServiceTests.Fixture.Input("Approved exact title"), "stable"), default));
        Assert.Empty(await f.Inner.Db.ActionProposals.ToArrayAsync());
    }

    [Fact]
    public async Task AgentApproverUsesTheSameExactPlanBoundary()
    {
        await using var f = await Fixture.Create();
        f.Manager.EmployeeType = EmployeeType.Agent; f.Manager.AgentInstallationId = Guid.NewGuid();
        await f.Inner.Db.SaveChangesAsync();
        var proposal = await f.Request();
        var handler = new PluginOperationsCapabilityHandler(f.Inner.Db, new Audit());
        var session = new AgentSession("session", "manager", f.Manager.AgentInstallationId.Value.ToString("D"),
            f.Inner.Organization.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([PluginOperationsCapabilityHandler.ManagedActionDecide]), 1));
        var request = new RequestCapability { RequestId = "decision", Capability = PluginOperationsCapabilityHandler.ManagedActionDecide,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(Decision(proposal), Json)) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal("Approved", f.Plan.Status);
        Assert.Null(f.Plan.ResultJson); // An approval response is not a provider execution result.
        _ = await f.RequireApproved();
    }

    [Fact]
    public async Task MutationRequiresApprovalChecksOwnershipAndPersistsOnce()
    {
        await using var f = await Fixture.Create(ownership: true);
        var transport = new MutationTransport(); var executor = f.Executor(transport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Execute(executor));
        Assert.Empty(transport.Requests);
        var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        await f.Execute(executor);
        Assert.Equal(new[] { "GET", "POST" }, transport.Requests);
        Assert.Equal("Completed", f.Plan.Status);
        Assert.Equal("{\"data\":\"value\"}", f.Plan.ResultJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(executor));
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("malformed")]
    [InlineData("error")]
    [InlineData("cancel")]
    public async Task UncertainMutationOutcomesCannotResend(string failure)
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        var transport = new MutationTransport { Failure = failure }; var executor = f.Executor(transport);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(executor));
        Assert.Equal("Indeterminate", f.Plan.Status); Assert.Null(f.Plan.ResultJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(executor));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task WrongOwnershipBlocksBeforePublicMutation()
    {
        await using var f = await Fixture.Create(ownership: true); var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        var transport = new MutationTransport { Owner = "another-account" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Execute(f.Executor(transport)));
        Assert.Equal("Blocked", f.Plan.Status);
        Assert.Equal("GET", Assert.Single(transport.Requests));
    }

    [Fact]
    public async Task InterruptedExecutingPlanIsNeverTreatedAsPermissionToRetry()
    {
        await using var f = await Fixture.Create(); var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        f.Plan.Status = "Executing"; f.Plan.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await f.Inner.Db.SaveChangesAsync(); var transport = new MutationTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(f.Executor(transport)));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(true, "Blocked", "resource_changed")]
    [InlineData(false, "Indeterminate", "reconciliation_required")]
    public async Task OnlyConfirmedConditionalFailureIsReportedAsResourceChanged(bool conditional, string status, string condition)
    {
        await using var f = await Fixture.Create(ownership: true, conditional: conditional);
        f.Inner.Requester.Grant!.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] {
            ConnectorPlanServiceTests.Fixture.Capability, PlatformCapabilities.ConnectorActionRead });
        await f.Inner.Db.SaveChangesAsync();
        var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        var transport = new MutationTransport { Failure = "precondition" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(f.Executor(transport)));
        Assert.Equal(status, f.Plan.Status); Assert.Null(f.Plan.ResultJson);
        Assert.Null(transport.Prepared[0].IfMatch); // Ownership reads must never inherit the mutation header.
        Assert.Equal(conditional ? "\"version-1\"" : null, transport.Prepared[1].IfMatch);
        var receipt = await new ConnectorActionService(f.Inner.Db, f.Inner.Service, f.Service)
            .ReadAsync(f.Inner.Organization, f.Inner.Requester.Id, new(proposal.Id), default);
        Assert.Equal(condition, receipt.ConditionCode);
        Assert.Equal(conditional ? 1 : 0, await f.Inner.Db.PluginOperationalStates.CountAsync(x => x.Kind == ConnectorMutationExecutor.PreconditionFailureKind));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(f.Executor(transport)));
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task ConditionalRequestDoesNotMakeUncertainOutcomesSafeToRetry(string failure)
    {
        await using var f = await Fixture.Create(conditional: true);
        var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        var transport = new MutationTransport { Failure = failure };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(f.Executor(transport)));
        Assert.Equal("Indeterminate", f.Plan.Status);
        Assert.Empty(await f.Inner.Db.PluginOperationalStates.Where(x => x.Kind == ConnectorMutationExecutor.PreconditionFailureKind).ToArrayAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(f.Executor(transport)));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task ApprovedConditionalPlanCannotReplaceItsVersion()
    {
        await using var f = await Fixture.Create(conditional: true);
        var proposal = await f.Request();
        await f.Service.DecideAsync(f.Inner.Organization, f.Manager.Id, Decision(proposal), default);
        f.Plan.PlanJson = f.Plan.PlanJson.Replace("version-1", "version-2", StringComparison.Ordinal);
        await f.Inner.Db.SaveChangesAsync();
        var transport = new MutationTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Execute(f.Executor(transport)));
        Assert.Empty(transport.Requests);
    }

    private sealed class MutationTransport : IConnectorHttpTransport
    {
        public Func<Task>? AfterOwnershipRead { get; init; }
        public string Owner { get; init; } = "confirmed";
        public string? Failure { get; init; }
        public List<string> Requests { get; } = [];
        public List<ConnectorPreparedRequest> Prepared { get; } = [];
        public async Task<ConnectorProviderResponse> SendAsync(Guid connectorId, Guid connectionId, ConnectorPreparedRequest request,
            Func<CancellationToken, Task> revalidate, CancellationToken ct)
        {
            await revalidate(ct); Requests.Add(request.Method); Prepared.Add(request);
            if (request.Method == "GET")
            {
                if (AfterOwnershipRead is not null) await AfterOwnershipRead();
                return new(200, JsonSerializer.SerializeToUtf8Bytes(new { owner = Owner }));
            }
            if (Failure == "timeout") throw new HttpRequestException("Unknown outcome");
            if (Failure == "cancel") throw new OperationCanceledException();
            return new(Failure == "precondition" ? 412 : Failure == "error" ? 503 : 200,
                Encoding.UTF8.GetBytes(Failure == "malformed" ? "{bad-json" : "{\"data\":\"value\"}"));
        }
    }
    private sealed class NoSecrets : IPluginSecretStore
    {
        public Task SetAsync(Guid installationId, string key, string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<string?> GetAsync(Guid installationId, string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task RemoveAsync(Guid installationId, string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private static DecideManagedAgentActionRequest Decision(ActionProposal proposal)
    {
        var binding = ConnectorActionApprovalService.Parse(proposal);
        return new(proposal.Id, "Approve", null, binding.PayloadHash, binding.ExpectedRevision,
            binding.IdempotencyKey, "decision-1", binding.ResourceId);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public ConnectorPlanServiceTests.Fixture Inner { get; private init; } = null!;
        public ConnectorExecution Plan { get; private set; } = null!;
        public OrganizationUser Employee { get; private set; } = null!;
        public OrganizationUser Manager { get; private set; } = null!;
        public ConnectorActionApprovalService Service => new(Inner.Db, Inner.Service, new Audit());
        public Task<ActionProposal> Request() => Service.RequestAsync(Inner.Organization, Inner.Requester.Id, Plan.Id, Plan.PlanHash, default);
        public Task<FrozenConnectorPlan> RequireApproved() => Service.RequireApprovedAsync(Inner.Organization, Inner.Requester.Id, Plan.Id, Plan.PlanHash, default);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public ConnectorMutationExecutor Executor(IConnectorHttpTransport transport) => new(Inner.Db, Service, transport, new NoSecrets(), new Audit());
        public Task Execute(ConnectorMutationExecutor executor) => executor.ExecuteAsync(Inner.Organization, Inner.Requester.Id, Plan.Id, Plan.PlanHash, default);
        public static async Task<Fixture> Create(bool ownership = false, bool responseBinding = false, bool conditional = false)
        {
            var f = new Fixture { Inner = await ConnectorPlanServiceTests.Fixture.Create(ownershipCheck: ownership, responseBinding: responseBinding) };
            var manifest = JsonSerializer.Deserialize<PluginManifest>(f.Inner.Connector.PackageVersion!.ManifestJson, Json)!;
            manifest = manifest with { Protocol = manifest.Protocol with { MinimumVersion = conditional ? "2.3" : manifest.Protocol.MinimumVersion },
                Provides = [manifest.Provides[0] with { Idempotency = "caller-key", Description = "Change the video title." }],
                ProviderOperations = [manifest.ProviderOperations[0] with { Effect = "write", Idempotency = "caller-key",
                    Http = manifest.ProviderOperations[0].Http! with { Method = conditional ? "PUT" : "POST", IfMatchInput = conditional ? "/search" : null,
                        BodyInputs = new Dictionary<string, string> { ["/title"] = "/search" } } }] };
            f.Inner.Connector.PackageVersion.ManifestJson = JsonSerializer.Serialize(manifest, Json);
            f.Manager = new() { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, DisplayName = "Assigned manager", EmployeeType = EmployeeType.Human };
            f.Employee = new() { Id = Guid.NewGuid(), OrganizationId = f.Inner.Organization, DisplayName = "Specialist",
                EmployeeType = EmployeeType.Agent, AgentInstallationId = f.Inner.Requester.Id, ReportsToOrganizationUserId = f.Manager.Id };
            f.Inner.Db.CoreOrganizationUsers.AddRange(f.Manager, f.Employee);
            await f.Inner.Db.SaveChangesAsync(); f.Plan = await f.Inner.Prepare(conditional ? "\"version-1\"" : "Approved exact title"); return f;
        }
    }
    private sealed class Audit : IAuditEventWriter
    {
        public Task WriteAsync(string type, string entity, Guid? id, string? summary, string? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
