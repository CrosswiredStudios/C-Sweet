using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class ProjectHealthTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{not json")]
    [InlineData("""{"workstreamId":null,"teamId":null}""")]
    [InlineData("""{"workstreamId":42}""")]
    [InlineData("""{"workstreamId":"not-a-guid"}""")]
    public void OptionalGuidReadsNeverThrow(string? json) =>
        Assert.Null(CSweetDbContext.ReadGuidProperty(json, "workstreamId"));

    [Fact]
    public async Task PersonalWorkWithNullContextScopesStillSaves()
    {
        // Regression: agents add personal work whose context serializes unset scopes as null.
        // Health capture used JsonElement.TryGetGuid on that null and aborted the save.
        await using var f = await Fixture.Create();
        var task = f.Ticket(WorkTaskStatus.Ready);
        task.BoardId = null;
        task.PersonalWorkContextJson = """{"workstreamId":null,"teamId":null,"boardId":null,"sourceFingerprint":null}""";
        f.Db.Add(task);
        await f.Db.SaveChangesAsync();
        Assert.Contains(f.Db.CoreWorkTasks, x => x.Id == task.Id);
        Assert.Equal(f.Project.Id, CSweetDbContext.ReadGuidProperty(
            $$"""{"workstreamId":"{{f.Project.Id}}"}""", "workstreamId"));
    }

    [Theory]
    [InlineData("product-manager")]
    [InlineData("game-producer")]
    [InlineData("warehouse-operations")]
    public async Task MonitoringUsesManagerBaseTypeInsteadOfDomainJobName(string role)
    {
        await using var f = await Fixture.Create();
        f.Producer.AgentInstallation!.PackageVersion!.ManifestJson = JsonSerializer.Serialize(new {
            rolePolicy = new { baseType = "manager", profile = "manager.v1", declaredRoleKeys = new[] { role } } });
        await f.Db.SaveChangesAsync(); await f.FailTicket();
        Assert.Equal(f.Producer.Id, Assert.Single(f.Db.ProjectIncidents).ProducerEmployeeId);
    }

    [Fact]
    public async Task JobNameAloneDoesNotGrantManagerMonitoring()
    {
        await using var f = await Fixture.Create();
        f.Producer.AgentInstallation!.PackageVersion!.ManifestJson = """{"rolePolicy":{"profile":"individual-contributor.v1","declaredRoleKeys":["manager","game-producer"]}}""";
        await f.Db.SaveChangesAsync(); await f.FailTicket(); Assert.Empty(f.Db.ProjectIncidents);
    }

    [Fact]
    public async Task BoundedRecoveryReviewPreservesDeadlineAndDoesNotResolveOrImmediatelyRewake()
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var incident = Assert.Single(f.Db.ProjectIncidents);
        var deadline = incident.EscalateAt;
        var report = new ReportManagementIncident(incident.Id, incident.Revision, "wait", "Observed failure", "Unknown", "Need recovery result", "Existing recovery requested")
        { Disposition = IncidentDispositions.AwaitingRecovery, ReviewAt = f.Clock.Now.AddMinutes(5), ActionReference = "existing-work:123" };
        await f.Service.ReportAsync(f.Org, f.Producer.Id, report, default);
        await f.Service.ReportAsync(f.Org, f.Producer.Id, report, default);
        Assert.Equal("Open", incident.Status); Assert.Equal(deadline, incident.EscalateAt); Assert.Single(f.Db.AgentPlatformEventOutbox);
        Assert.Empty((await f.Service.ReadIncidentsAsync(f.Org, f.Producer.Id, new(), default)).Incidents);
        f.Clock.Now += TimeSpan.FromMinutes(5); await f.Review();
        Assert.Null(incident.ReviewAt); Assert.Equal(2, f.Db.AgentPlatformEventOutbox.Count());
        Assert.Single((await f.Service.ReadIncidentsAsync(f.Org, f.Producer.Id, new(), default)).Incidents);
        f.Clock.Now += TimeSpan.FromMinutes(10); await f.Review();
        Assert.Equal(f.Director.Id, incident.CurrentRecipientId); Assert.Equal("existing-work:123", incident.ActionReference);
    }

    [Fact]
    public async Task ClaimedRecoveryCannotExtendDeadlineOrWriteAnInvalidReceipt()
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var incident = Assert.Single(f.Db.ProjectIncidents);
        var report = new ReportManagementIncident(incident.Id, incident.Revision, "invalid", "Fact", "Cause", "Missing", "Action")
        { Disposition = IncidentDispositions.Investigating, ReviewAt = f.Clock.Now.AddHours(1) };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.ReportAsync(f.Org, f.Producer.Id, report, default));
        await f.Db.SaveChangesAsync(); Assert.Empty(f.Db.ProjectIncidentReceipts);
    }
    [Fact]
    public async Task ForwardedIncidentsLeaveTheBoundedProducerRecoveryQueue()
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var first = f.Db.ProjectIncidents.Single();
        await f.Service.ForwardAsync(f.Org, f.Producer.Id, new(first.Id, first.Revision, "forward", "Manager action needed"), default);
        var secondTask = await f.FailTicket();
        var page = await f.Service.ReadIncidentsAsync(f.Org, f.Producer.Id, new(Limit: 1), default);
        Assert.Equal(secondTask.Id, Assert.Single(page.Incidents).AffectedWorkItemId);
        var projectPage = await f.Service.ReadIncidentsAsync(f.Org, f.Producer.Id, new(WorkstreamId: f.Project.Id), default);
        Assert.Equal(2, projectPage.Incidents.Count);
    }

    [Theory]
    [InlineData(AgentRuntimeStatus.StartFailed)]
    [InlineData(AgentRuntimeStatus.PolicyDenied)]
    [InlineData(AgentRuntimeStatus.McpSessionTimedOut)]
    public async Task RuntimeStartupFailuresAreImmediateAndPreserveExactAffectedWork(AgentRuntimeStatus status)
    {
        await using var f = await Fixture.Create(); var ticket = f.Ticket(WorkTaskStatus.Ready); f.Db.Add(ticket);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = f.Producer.AgentInstallationId!.Value,
            Name = "work.execution.run", Status = AgentWorkStatus.Pending, CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddHours(1) };
        var runtime = new AgentRuntimeInstance { Id = Guid.NewGuid(), AgentInstallationId = work.AgentInstallationId, QueuedAt = f.Clock.Now };
        if (status != AgentRuntimeStatus.PolicyDenied) runtime.TransitionTo(AgentRuntimeStatus.Starting, f.Clock.Now);
        if (status == AgentRuntimeStatus.McpSessionTimedOut) runtime.TransitionTo(AgentRuntimeStatus.WaitingForMcpSession, f.Clock.Now);
        runtime.TransitionTo(status, f.Clock.Now, "Correlated runtime could not accept work");
        f.Db.AddRange(work, runtime, new CSweet.Domain.Analytics.WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = ticket.Id });
        await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal(ticket.Id, incident.AffectedWorkItemId); Assert.Contains(status.ToString(), incident.EvidenceJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IntentionalPauseOrPendingApprovalIsNotUnexplainedInactivity(bool paused)
    {
        await using var f = await Fixture.Create();
        if (paused) f.Project.LifecycleStage = "Paused";
        else f.Db.Add(f.Ticket(WorkTaskStatus.WaitingForApproval));
        await f.Db.SaveChangesAsync(); await f.Review();
        f.Clock.Now += TimeSpan.FromHours(1); await f.Review();
        Assert.Empty(f.Db.ProjectIncidents); Assert.NotNull(f.Db.ProjectHealthStates.Single().WaitingReason);
    }

    [Fact]
    public async Task MutableStatusUpdatesAndRepeatedFailuresDoNotResetProgress()
    {
        await using var f = await Fixture.Create(); var ticket = f.Ticket(WorkTaskStatus.Completed); f.Db.Add(ticket);
        var artifact = new Artifact { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            Title = "Plan", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.Add(artifact); await f.Db.SaveChangesAsync(); await f.Review();
        var progress = f.Db.ProjectHealthStates.Single().LastProgressAt;
        f.Clock.Now += TimeSpan.FromMinutes(15);
        ticket.UpdatedAt = f.Clock.Now; artifact.UpdatedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal(progress, f.Db.ProjectHealthStates.Single().LastProgressAt);
        Assert.Equal("idle", Assert.Single(f.Db.ProjectIncidents).Fingerprint);
    }

    [Fact]
    public async Task DiagnosticFailureAttachesToOriginalIncidentWithoutRecursiveWakeOrExtendedDeadline()
    {
        await using var f = await Fixture.Create(); await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents); var deadline = incident.EscalateAt;
        var wake = Assert.Single(f.Db.AgentPlatformEventOutbox);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = f.Producer.AgentInstallationId!.Value,
            Name = ProjectHealthEvents.ReviewDue, SourceType = "platform-event", SourceId = wake.Id.ToString(), CreatedAt = f.Clock.Now };
        f.Db.Add(work); f.Db.Add(new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1,
            Error = "transport_failure password=secret", FinishedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Single(f.Db.ProjectIncidents); Assert.Single(f.Db.AgentPlatformEventOutbox);
        Assert.Equal(deadline, incident.EscalateAt); Assert.Contains("transport_failure", incident.EvidenceJson);
        Assert.DoesNotContain("secret", incident.EvidenceJson);
        f.Clock.Now += TimeSpan.FromMinutes(15); await f.Review();
        Assert.Equal(f.Director.Id, incident.CurrentRecipientId); Assert.Contains("transport_failure", incident.MissingEvidence);
    }

    [Fact]
    public async Task ExecutionFailureIdentifiesTicketCoalescesItsSymptomAndResolvesOnCompletion()
    {
        await using var f = await Fixture.Create(); var ticket = f.Ticket(WorkTaskStatus.Failed); f.Db.Add(ticket);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = f.Producer.AgentInstallationId!.Value,
            Name = "work.execution.run", Status = AgentWorkStatus.DeadLetter, LastError = "schema validation failed", CreatedAt = f.Clock.Now };
        f.Db.Add(work); f.Db.Add(new CSweet.Domain.Analytics.WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = ticket.Id });
        await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents); Assert.Equal(ticket.Id, incident.AffectedWorkItemId);
        ticket.Status = WorkTaskStatus.Completed; await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Resolved", incident.Status); Assert.Single(f.Db.ProjectIncidents);
    }

    [Fact]
    public void TypedIncidentRequestsMatchTheGrantedMcpSchemas()
    {
        var catalog = new CSweet.AgentHost.Broker.McpToolCatalog([]);
        var requests = new Dictionary<string, object> {
            [ProjectHealthCapabilities.Read] = new ReadProjectHealth(Guid.NewGuid()),
            [ProjectHealthCapabilities.Diagnostics] = new ReadProjectDiagnostics(Guid.NewGuid()),
            [ProjectHealthCapabilities.Incidents] = new ReadManagementIncidents(),
            [ProjectHealthCapabilities.Report] = new ReportManagementIncident(Guid.NewGuid(), 1, "key", "facts", "cause", "missing", "action"),
            [ProjectHealthCapabilities.Forward] = new ForwardManagementIncident(Guid.NewGuid(), 1, "key", "reason") };
        foreach (var tool in catalog.List(requests.Keys.ToHashSet()))
            CSweet.AgentHost.Broker.JsonSchemaValidator.Validate(JsonSerializer.SerializeToElement(requests[tool.Capability], new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        Assert.Equal(5, catalog.List(requests.Keys.ToHashSet()).Count);
    }

    [Fact]
    public async Task RepositorySetupFailureIsImmediateBeforeAnyBoardWorkExists()
    {
        await using var f = await Fixture.Create();
        f.Db.RepositoryProvisioningRequests.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            RequestedByOrganizationUserId = f.Producer.Id, Status = RepositoryProvisioningStatus.Failed,
            FailureCode = "schema_invalid", FailureMessage = "Response missing repositoryId", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Project repository setup failed", Assert.Single(f.Db.ProjectIncidents).Reason);
    }

    [Fact]
    public async Task ReparentedManagerDoesNotRouteIntoTheOldReportingBranch()
    {
        await using var f = await Fixture.Create(); await f.FailTicket();
        var incident = f.Db.ProjectIncidents.Single();
        await f.Service.ForwardAsync(f.Org, f.Producer.Id, new(incident.Id, incident.Revision, "up", "Need platform help"), default);
        f.Producer.ReportsToOrganizationUserId = f.Ceo.Id; await f.Db.SaveChangesAsync();
        f.Clock.Now += TimeSpan.FromMinutes(15); await f.Review();
        Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
    }

    [Fact]
    public async Task IdleBoundaryAndRepeatedReviewsKeepOneIncidentAndDeadline()
    {
        await using var f = await Fixture.Create();
        await f.Review(); Assert.Empty(f.Db.ProjectIncidents);
        f.Clock.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1);
        await f.Review(); Assert.Empty(f.Db.ProjectIncidents);
        f.Clock.Now += TimeSpan.FromSeconds(1);
        await f.Review(); var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal("idle", incident.Fingerprint);
        var deadline = incident.EscalateAt;
        await f.Review(); Assert.Single(f.Db.ProjectIncidents); Assert.Equal(deadline, incident.EscalateAt);
        Assert.Single(f.Db.AgentPlatformEventOutbox.Where(x => x.EventType == ProjectHealthEvents.ReviewDue));
    }

    [Fact]
    public async Task OfflineProducerAndManagersReachCeoWithSameIncidentAndSingleDelivery()
    {
        await using var f = await Fixture.Create();
        await f.Review(); f.Clock.Now += TimeSpan.FromMinutes(15); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        foreach (var recipient in new[] { f.Director.Id, f.Chief.Id, f.Ceo.Id })
        {
            f.Clock.Now += TimeSpan.FromMinutes(15); await f.Review();
            Assert.Equal(recipient, incident.CurrentRecipientId);
        }
        Assert.Null(incident.EscalateAt);
        await RecordOwnerRequirement(f, incident);
        await f.Service.DeliverAsync(default); await f.Service.DeliverAsync(default);
        Assert.Single(f.Db.CoreConversationMessages); Assert.Single(f.Db.UserNotifications);
        Assert.Contains(f.Db.ApplicationRealtimeOutbox, x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.ProjectIncidentChanged &&
            x.RecipientOrganizationUserId == f.Ceo.Id);
        Assert.Equal(incident.Id, f.Db.CoreConversationMessages.Single().CorrelationId);
        Assert.Equal(3, JsonSerializer.Deserialize<ManagementIncidentHop[]>(incident.HistoryJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Length);
    }

    [Fact]
    public async Task ReportAndForwardAreIdempotentAndStaleMutationsCannotExtendDeadline()
    {
        await using var f = await Fixture.Create();
        await f.FailTicket(); var incident = Assert.Single(f.Db.ProjectIncidents);
        var report = new ReportManagementIncident(incident.Id, incident.Revision, "diagnosis", "Schema validation failed.", "Contract mismatch is likely.", "Exact field unavailable.", "Repair the integration.");
        await f.Service.ReportAsync(f.Org, f.Producer.Id, report, default);
        await f.Service.ReportAsync(f.Org, f.Producer.Id, report, default);
        Assert.Equal(f.Director.Id, incident.CurrentRecipientId);
        var deadline = incident.EscalateAt;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ForwardAsync(f.Org, f.Producer.Id,
            new(incident.Id, report.ExpectedRevision, "stale", "Forward"), default));
        Assert.Equal(deadline, incident.EscalateAt);
        await f.Service.ForwardAsync(f.Org, f.Director.Id, new(incident.Id, incident.Revision, "director", "Platform schema errors are outside creative direction."), default);
        await f.Service.ForwardAsync(f.Org, f.Chief.Id, new(incident.Id, incident.Revision, "chief", "Owner action is needed."), default);
        Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
    }

    [Fact]
    public async Task RunningLabelDoesNotSuppressStallButRecentAttemptProgressDoes()
    {
        await using var f = await Fixture.Create();
        var ticket = f.Ticket(WorkTaskStatus.Running); f.Db.Add(ticket); await f.Db.SaveChangesAsync();
        await f.Review(); f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        Assert.Single(f.Db.ProjectIncidents);
        await using var live = await Fixture.Create();
        var task = live.Ticket(WorkTaskStatus.Running); live.Db.Add(task);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = live.Org.ToString(), AgentInstallationId = live.Producer.AgentInstallationId!.Value,
            Name = "work.execution.run", Status = AgentWorkStatus.Leased, AttemptCount = 1, CreatedAt = live.Clock.Now, DeadlineAt = live.Clock.Now.AddHours(1) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1, ClaimedAt = live.Clock.Now, LeaseExpiresAt = live.Clock.Now.AddHours(1) };
        live.Db.AddRange(work, attempt, new CSweet.Domain.Analytics.WorkExecutionContext { Id = attempt.Id, OrganizationId = live.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = task.Id });
        await live.Db.SaveChangesAsync(); await live.Review(); live.Clock.Now += TimeSpan.FromMinutes(16);
        live.Db.Add(new AgentWorkProgress { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, AgentWorkAttemptId = attempt.Id,
            Sequence = 1, OccurredAt = live.Clock.Now });
        await live.Db.SaveChangesAsync(); await live.Review();
        Assert.Empty(live.Db.ProjectIncidents);
    }

    [Fact]
    public async Task RecordedWaitSuppressesIdleUntilItsDeadline()
    {
        await using var f = await Fixture.Create();
        var ticket = f.Ticket(WorkTaskStatus.Blocked); ticket.NextReviewAt = f.Clock.Now.AddHours(1); ticket.WaitingReason = "Scheduled dependency";
        f.Db.Add(ticket); await f.Db.SaveChangesAsync(); await f.Review();
        f.Clock.Now += TimeSpan.FromMinutes(20); await f.Review(); Assert.Empty(f.Db.ProjectIncidents);
        f.Clock.Now += TimeSpan.FromMinutes(41); await f.Review(); Assert.Single(f.Db.ProjectIncidents);
    }

    [Fact]
    public async Task FailureReportsImmediatelyAndRecoveryClosesThenRecurrenceStartsNewEpisode()
    {
        await using var f = await Fixture.Create();
        var ticket = await f.FailTicket(); var first = Assert.Single(f.Db.ProjectIncidents);
        ticket.Status = WorkTaskStatus.Completed; ticket.UpdatedAt = f.Clock.Now.AddMinutes(1); f.Clock.Now = ticket.UpdatedAt;
        await f.Db.SaveChangesAsync(); await f.Review(); Assert.Equal("Resolved", first.Status);
        ticket.Status = WorkTaskStatus.Failed; ticket.BlockReason = "Another invalid payload"; await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal(2, f.Db.ProjectIncidents.Count()); Assert.Single(f.Db.ProjectIncidents.Where(x => x.Status == "Open"));
    }

    [Fact]
    public async Task RevokedGrantUnrelatedActorAndCrossTenantAreDenied()
    {
        await using var f = await Fixture.Create(); await f.FailTicket(); var incident = f.Db.ProjectIncidents.Single();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadDiagnosticsAsync(Guid.NewGuid(), f.Producer.Id, new(incident.Id), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadDiagnosticsAsync(f.Org, Guid.NewGuid(), new(incident.Id), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequireActorAsync(f.Org, f.Producer.AgentInstallationId!.Value, ProjectHealthCapabilities.Diagnostics, default));
        f.Producer.ReportsToOrganizationUserId = f.Ceo.Id; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadDiagnosticsAsync(f.Org, f.Director.Id, new(incident.Id), default));
    }

    [Fact]
    public async Task CycleFallsBackToHumanOwnerAndSecretsAreRedacted()
    {
        await using var f = await Fixture.Create(); f.Producer.ReportsToOrganizationUserId = f.Producer.Id;
        var ticket = f.Ticket(WorkTaskStatus.Failed); ticket.BlockReason = "schema failure password=supersecret"; f.Db.Add(ticket);
        await f.Db.SaveChangesAsync(); await f.Review(); var incident = f.Db.ProjectIncidents.Single();
        Assert.DoesNotContain("supersecret", incident.EvidenceJson);
        f.Clock.Now += TimeSpan.FromMinutes(15); await f.Review(); Assert.Equal(f.Ceo.Id, incident.CurrentRecipientId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearedDecisionWaitCannotHideBehindRenewedDeadlinesOrUnrelatedProgress(bool cleared)
    {
        await using var f = await Fixture.Create();
        var decision = new WorkstreamDecisionRecord { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, Status = cleared ? "Decided" : "Pending", UpdatedAt = f.Clock.Now };
        var task = f.Ticket(WorkTaskStatus.Running); task.NextReviewAt = f.Clock.Now.AddHours(1);
        task.WaitingReason = "Waiting for the project decision";
        task.PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = f.Project.Id, decisionId = decision.Id });
        f.Db.AddRange(decision, task); await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Empty(f.Db.ProjectIncidents);
        f.Clock.Now += TimeSpan.FromMinutes(16);
        task.NextReviewAt = f.Clock.Now.AddHours(1); task.UpdatedAt = f.Clock.Now;
        f.Db.Add(f.Ticket(WorkTaskStatus.Completed)); await f.Db.SaveChangesAsync(); await f.Review();
        if (!cleared) { Assert.Empty(f.Db.ProjectIncidents); return; }
        var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal($"dependency-wait:{task.Id:N}", incident.Fingerprint);
        var deadline = incident.EscalateAt;
        task.NextReviewAt = f.Clock.Now.AddHours(2); await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Single(f.Db.ProjectIncidents); Assert.Equal(deadline, incident.EscalateAt);
        task.WaitingReason = null; task.NextReviewAt = null; task.Status = WorkTaskStatus.Ready;
        await f.Db.SaveChangesAsync(); await f.Review(); Assert.Equal("Resolved", incident.Status);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task HiringWaitChecksFulfillmentAndCurrentTeamReadiness(bool fulfilled, bool ready, bool stalled)
    {
        await using var f = await Fixture.Create(); var team = Guid.NewGuid();
        var task = f.Ticket(WorkTaskStatus.Running); task.AssignedEmployeeId = f.Producer.Id;
        task.AssignedAgentInstallationId = f.Producer.AgentInstallationId;
        task.WaitingReason = "The initial coverage proposal is awaiting hiring."; task.NextReviewAt = f.Clock.Now.AddHours(1);
        task.PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = f.Project.Id, teamId = team });
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            TeamId = team, RequestingInstallationId = f.Producer.AgentInstallationId!.Value,
            Status = ProposalStatus.Approved, Headcount = 1, FulfilledHeadcount = fulfilled ? 1 : 0 };
        f.Producer.AgentInstallation!.IsEnabled = ready;
        f.Db.AddRange(task, plan, new OrganizationTeam { Id = team, OrganizationId = f.Org, LeadOrganizationUserId = f.Producer.Id },
            new WorkstreamTeamAssignmentRecord { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, TeamId = team, StartsAt = f.Clock.Now },
            new TeamMembership { Id = Guid.NewGuid(), OrganizationId = f.Org, TeamId = team,
                OrganizationUserId = f.Producer.Id, JoinedAt = f.Clock.Now },
            new HiringRecommendationFulfillment { Id = Guid.NewGuid(), OrganizationId = f.Org,
                WorkforcePlanId = plan.Id, ResultOrganizationUserId = f.Producer.Id, CreatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync(); await f.Review(); f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        if (stalled) Assert.Equal($"dependency-wait:{task.Id:N}", Assert.Single(f.Db.ProjectIncidents).Fingerprint);
        else Assert.Empty(f.Db.ProjectIncidents);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("intake")]
    [InlineData("task")]
    [InlineData("delivery")]
    [InlineData("context")]
    public async Task DirectChatInterruptionUsesExactProjectSourcesAndSuccessfulRetryRecovery(string source)
    {
        await using var f = await Fixture.Create();
        var chat = new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Org, Kind = ConversationKind.DirectHumanAgent };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = chat.Id,
            UserMessageId = Guid.NewGuid(), Status = ChatTurnStatus.Running, CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        var unrelated = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = chat.Id,
            UserMessageId = Guid.NewGuid(), Status = ChatTurnStatus.CompletedWithWarnings,
            ErrorCode = "memory.runtime_reset", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.AddRange(chat, turn, unrelated); await f.Db.SaveChangesAsync();
        AgentWorkItem? scopedWork = null;
        if (source == "session") f.Db.Add(new CSweet.Domain.Communications.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, SourceChatTurnId = turn.Id, Status = CSweet.Domain.Communications.AgentCoordinationStatus.Completed, CompletedAt = f.Clock.Now });
        if (source == "intake") f.Db.Add(new ProjectIntake { Id = Guid.NewGuid(), OrganizationId = f.Org,
            ProjectId = f.Project.Id, SourceChatTurnId = turn.Id });
        if (source == "task")
        { var task = f.Ticket(WorkTaskStatus.Completed); task.SourceConversationId = chat.Id; task.SourceMessageId = turn.UserMessageId; f.Db.Add(task); }
        if (source is "delivery" or "context")
        {
            var runtime = Guid.NewGuid(); var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
                AgentInstallationId = f.Producer.AgentInstallationId!.Value, SourceType = "chat-turn", SourceId = turn.Id.ToString(),
                DeadlineAt = f.Clock.Now.AddHours(1) };
            scopedWork = work;
            f.Db.AddRange(work, new AgentWorkAttempt { Id = Guid.NewGuid(), RuntimeInstanceId = runtime,
                AgentWorkItemId = work.Id, LeaseExpiresAt = f.Clock.Now.AddHours(1) }); await f.Db.SaveChangesAsync();
            if (source == "context")
            {
                var completed = f.Ticket(WorkTaskStatus.Completed); f.Db.Add(completed);
                f.Db.Add(new CSweet.Domain.Analytics.WorkExecutionContext { Id = Guid.NewGuid(), OrganizationId = f.Org,
                    AgentWorkItemId = work.Id, AgentInstallationId = work.AgentInstallationId, RootWorkItemId = completed.Id });
                await f.Db.SaveChangesAsync();
            }
            else
            {
                await ProjectChatTurnAttribution.RecordAsync(f.Db, f.Org, work.AgentInstallationId, runtime, f.Project.Id, f.Clock.Now, default);
                await ProjectChatTurnAttribution.RecordAsync(f.Db, f.Org, work.AgentInstallationId, runtime, f.Project.Id, f.Clock.Now, default);
                Assert.Single(f.Db.ProjectHealthSignals.Where(x => x.SourceKind == ProjectChatTurnAttribution.SourceKind));
            }
        }
        await f.Db.SaveChangesAsync(); turn.Status = ChatTurnStatus.CompletedWithWarnings; turn.ErrorCode = "memory.runtime_reset";
        if (scopedWork is not null) { scopedWork.Status = AgentWorkStatus.DeadLetter; scopedWork.LastError = MemoryRuntimeResetRequiredException.SafeMessage; }
        await f.Db.SaveChangesAsync(); Assert.Contains(f.Db.ProjectHealthSignals, x => x.SourceKind == nameof(ChatTurn) && x.SourceId == turn.Id);
        await f.Review(); var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal($"chat:{turn.Id:N}", incident.Fingerprint);
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.Contains("memory context", presentation.Summary); Assert.Contains(chat.Id.ToString(), presentation.Link);
        var retry = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = chat.Id,
            RetryOfTurnId = turn.Id, UserMessageId = Guid.NewGuid(), Status = ChatTurnStatus.CompletedWithWarnings, ErrorCode = "memory.runtime_reset" };
        f.Db.Add(retry); await f.Db.SaveChangesAsync(); await f.Review(); Assert.Equal("Open", incident.Status);
        f.Db.Add(new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org, ConversationId = chat.Id,
            RetryOfTurnId = retry.Id, Status = ChatTurnStatus.Completed, UserMessageId = Guid.NewGuid() });
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Resolved", incident.Status);
        Assert.DoesNotContain(f.Db.ProjectIncidents, x => x.Fingerprint == $"chat:{unrelated.Id:N}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompletedPlanningWaitRequiresTheExactCycleAndAllParticipants(bool exact)
    {
        await using var f = await Fixture.Create(); var team = Guid.NewGuid();
        var task = f.Ticket(WorkTaskStatus.Running); task.AssignedEmployeeId = f.Producer.Id;
        task.AssignedAgentInstallationId = f.Producer.AgentInstallationId; task.WaitingReason = "Waiting for both role authorities to complete their correlated planning proposals.";
        task.NextReviewAt = f.Clock.Now.AddHours(1);
        task.PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = f.Project.Id, teamId = team,
            boardId = f.Board.Id, sourceFingerprint = "current-cycle" });
        var session = new CSweet.Domain.Communications.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, TeamId = team, SourceBoardId = f.Board.Id, SourceKind = "Board",
            InitiatorOrganizationUserId = f.Producer.Id, InitiatorInstallationId = f.Producer.AgentInstallationId!.Value,
            Status = CSweet.Domain.Communications.AgentCoordinationStatus.Completed, CompletedAt = f.Clock.Now };
        f.Db.AddRange(task, session, new CSweet.Domain.Communications.AgentCoordinationTurn { Id = Guid.NewGuid(),
            SessionId = session.Id, SpeakerOrganizationUserId = f.Producer.Id, ArtifactKey = exact ? "current-cycle" : "previous-cycle" });
        await f.Db.SaveChangesAsync(); await f.Review(); f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        if (exact) Assert.Equal($"dependency-wait:{task.Id:N}", Assert.Single(f.Db.ProjectIncidents).Fingerprint);
        else Assert.Empty(f.Db.ProjectIncidents);
    }

    [Fact]
    public async Task ProjectChatScopeRejectsAmbiguousExpiredAndCrossTenantDeliveries()
    {
        await using var f = await Fixture.Create(); var runtime = Guid.NewGuid();
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Org };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, SourceType = "chat-turn", SourceId = turn.Id.ToString() };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id,
            RuntimeInstanceId = runtime, LeaseExpiresAt = f.Clock.Now.AddMinutes(-1) };
        f.Db.AddRange(turn, work, attempt); await f.Db.SaveChangesAsync();
        await ProjectChatTurnAttribution.RecordAsync(f.Db, f.Org, work.AgentInstallationId, runtime, f.Project.Id, f.Clock.Now, default);
        attempt.LeaseExpiresAt = f.Clock.Now.AddHours(1); await f.Db.SaveChangesAsync();
        await ProjectChatTurnAttribution.RecordAsync(f.Db, Guid.NewGuid(), work.AgentInstallationId, runtime, f.Project.Id, f.Clock.Now, default);
        var other = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = work.AgentInstallationId };
        f.Db.AddRange(other, new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = other.Id,
            RuntimeInstanceId = runtime, LeaseExpiresAt = f.Clock.Now.AddHours(1) }); await f.Db.SaveChangesAsync();
        await ProjectChatTurnAttribution.RecordAsync(f.Db, f.Org, work.AgentInstallationId, runtime, f.Project.Id, f.Clock.Now, default);
        Assert.DoesNotContain(f.Db.ProjectHealthSignals, x => x.SourceKind == ProjectChatTurnAttribution.SourceKind);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Theory]
    [InlineData("Delivery planning needs attention: independent review assignment rejected")]
    [InlineData("Workflow staffing needs attention: Delivery planning needs attention: Delivery branch operations are unavailable.")]
    public async Task PlanningConflictReportsEvenWhenRunningAndRepeatedReviewMovesTheDeadline(string reason)
    {
        await using var f = await Fixture.Create();
        var ticket = f.Ticket(WorkTaskStatus.Running); ticket.NextReviewAt = f.Clock.Now.AddMinutes(20);
        ticket.WaitingReason = reason;
        f.Db.Add(ticket); await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        var deadline = incident.EscalateAt;
        ticket.NextReviewAt = f.Clock.Now.AddHours(1); await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Single(f.Db.ProjectIncidents); Assert.Equal(deadline, incident.EscalateAt);
        ticket.WaitingReason = null; ticket.NextReviewAt = null;
        await f.Db.SaveChangesAsync(); await f.Review(); Assert.Equal("Resolved", incident.Status);
    }

    [Fact]
    public async Task OutdatedDeliveryScopeCreatesOneIncidentDespiteScheduledRecoveryAndResolvesFromCurrentPins()
    {
        await using var f = await Fixture.Create();
        var parent = f.Ticket(WorkTaskStatus.Ready); parent.Kind = WorkItemKind.Story; parent.PlanningRevision = 2;
        var child = f.Ticket(WorkTaskStatus.Ready); child.ParentWorkTaskId = parent.Id; child.PlanningRevision = 4;
        var retry = f.Ticket(WorkTaskStatus.Running); retry.WaitingReason = "Delivery planning needs attention: scope changed";
        retry.NextReviewAt = f.Clock.Now.AddMinutes(20);
        var snapshot = new CSweet.WorkManagement.Contracts.WorkDeliveryScopeSnapshot("Story", parent.Id, f.Board.Id,
            1, [child.Id], new(["Requirement"], ["Verified"]), [])
            { ChildPlanningRevisions = new Dictionary<Guid, long> { [child.Id] = 2 } };
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var plan = new WorkDeliveryPlan { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            ManagerOrganizationUserId = f.Producer.Id, ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { snapshot }, json),
            CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.AddRange(parent, child, retry, plan); await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal($"delivery-plan:{plan.Id:N}", incident.Fingerprint);
        Assert.Contains("pinned planning revision 2, current 4", incident.Facts);
        await f.Review(); Assert.Single(f.Db.ProjectIncidents);
        Assert.Contains(f.Db.ApplicationRealtimeOutbox, x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.ProjectIncidentChanged);
        plan.ScopesJson = System.Text.Json.JsonSerializer.Serialize(new[] { snapshot with { PlanningRevision = 2,
            ChildPlanningRevisions = new Dictionary<Guid, long> { [child.Id] = 4 } } }, json);
        plan.Status = "Active";
        await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Resolved", incident.Status);
    }

    [Fact]
    public async Task BrokenAssessmentEscalatesOnceAndOnlySuccessfulAssessmentResolvesIt()
    {
        await using var f = await Fixture.Create();
        f.Director.ReportsToOrganizationUserId = f.Ceo.Id;
        var plan = new WorkDeliveryPlan { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            ManagerOrganizationUserId = f.Producer.Id, ScopesJson = "{}", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        f.Db.Add(plan); await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        var deadline = incident.EscalateAt;
        Assert.Equal("health-assessment", incident.Fingerprint);
        Assert.Equal(f.Producer.Id, incident.CurrentRecipientId);
        await f.Review();
        incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal(deadline, incident.EscalateAt);
        Assert.Single(f.Db.AgentPlatformEventOutbox);
        foreach (var recipient in new[] { f.Director.Id, f.Ceo.Id })
        {
            f.Clock.Now += TimeSpan.FromMinutes(15); await f.Review();
            incident = Assert.Single(f.Db.ProjectIncidents);
            Assert.Equal(recipient, incident.CurrentRecipientId);
        }
        await RecordOwnerRequirement(f, incident);
        await f.Service.DeliverAsync(default); await f.Service.DeliverAsync(default);
        Assert.Equal(f.Director.Id, Assert.Single(f.Db.CoreConversationMessages).SenderOrganizationUserId);
        Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        var savedPlan = await f.Db.WorkDeliveryPlans.SingleAsync();
        savedPlan.Status = "Cancelled"; await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Resolved", (await f.Db.ProjectIncidents.SingleAsync(x => x.Id == incident.Id)).Status);
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
    }

    [Fact]
    public async Task FailedCollaborationHasOneEpisodeAcrossClearedPointersRetriesAndCompletion()
    {
        await using var f = await Fixture.Create();
        var session = new CSweet.Domain.Communications.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, ConversationId = Guid.NewGuid(), Subject = "Technical planning",
            InitiatorOrganizationUserId = f.Producer.Id, Status = CSweet.Domain.Communications.AgentCoordinationStatus.Failed,
            FinalSummary = "llm.request_failed", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = f.Producer.AgentInstallationId!.Value,
            Name = "planning", SourceType = "agent-coordination", CorrelationId = session.Id.ToString(), CausationId = Guid.NewGuid().ToString(),
            Status = AgentWorkStatus.DeadLetter, AttemptCount = 1, CreatedAt = f.Clock.Now, LastError = "agent-failure:v1;code=llm.request_failed;retryable=false" };
        f.Db.AddRange(session, work); await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        Assert.Equal($"coordination:{session.Id:N}", incident.Fingerprint);
        Assert.Contains("AgentCoordinationSession", incident.EvidenceJson);
        var deadline = incident.EscalateAt;
        // Closing the originating ticket cannot establish that its failed collaboration recovered.
        var ticket = f.Ticket(WorkTaskStatus.Completed);
        session.SourceWorkItemId = ticket.Id;
        session.CurrentAgentWorkItemId = work.Id;
        f.Db.Add(ticket); await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Open", incident.Status); Assert.Single(f.Db.ProjectIncidents);
        session.Status = CSweet.Domain.Communications.AgentCoordinationStatus.Active;
        var retry = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = work.AgentInstallationId,
            Name = work.Name, SourceType = work.SourceType, CorrelationId = work.CorrelationId, CausationId = work.CausationId,
            Status = AgentWorkStatus.Pending, CreatedAt = f.Clock.Now.AddSeconds(1), DeadlineAt = f.Clock.Now.AddHours(1) };
        f.Db.Add(retry); await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Open", incident.Status); Assert.Equal(deadline, incident.EscalateAt);
        Assert.Contains("queued", (await f.Service.PresentAsync(incident, default)).Recovery);
        session.Status = CSweet.Domain.Communications.AgentCoordinationStatus.Completed;
        session.CompletedAt = f.Clock.Now; await f.Db.SaveChangesAsync(); await f.Review();
        Assert.Equal("Resolved", incident.Status); Assert.Single(f.Db.ProjectIncidents);
    }

    [Fact]
    public async Task LegacyDuplicateIsSupersededAndOwnerGetsCompactActionableReportWithoutEvidenceSpam()
    {
        await using var f = await Fixture.Create();
        var session = new CSweet.Domain.Communications.AgentCoordinationSession { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, ConversationId = Guid.NewGuid(), InitiatorOrganizationUserId = f.Producer.Id,
            Subject = "Technical planning", Status = CSweet.Domain.Communications.AgentCoordinationStatus.Failed,
            FinalSummary = "llm.request_failed", CreatedAt = f.Clock.Now, UpdatedAt = f.Clock.Now };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(), AgentInstallationId = f.Producer.AgentInstallationId!.Value,
            SourceType = "agent-coordination", CorrelationId = session.Id.ToString(), Status = AgentWorkStatus.DeadLetter, AttemptCount = 1,
            LastError = "agent-failure:v1;code=llm.request_failed;retryable=false", CreatedAt = f.Clock.Now };
        ProjectIncident Old(string key) => new() { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Project.Id,
            ProducerEmployeeId = f.Producer.Id, CurrentRecipientId = f.Ceo.Id, Fingerprint = key, DetectedAt = f.Clock.Now,
            Facts = new string('x', 8000) + "model.response.chunk", RecommendedAction = "My responsibility is creative direction; manager review is needed." };
        var canonical = Old($"coordination:{session.Id:N}"); var duplicate = Old($"work:{work.Id:N}");
        f.Db.AddRange(session, work, canonical, duplicate);
        await f.Db.SaveChangesAsync(); await f.Review(); await RecordOwnerRequirement(f, canonical); await f.Service.DeliverAsync(default);
        Assert.Equal("Superseded", duplicate.Status); Assert.Equal("Open", canonical.Status);
        Assert.Null(duplicate.ResolvedAt);
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Equal(canonical.Id, attention.Id);
        var collaboration = Assert.IsType<CSweet.Contracts.Core.ProjectIncidentCollaborationResponse>(attention.Collaboration);
        Assert.Equal(session.Id, collaboration.SessionId); Assert.Equal(session.ConversationId, collaboration.ConversationId);
        Assert.Equal(session.Revision, collaboration.Revision); Assert.True(collaboration.CanRetry);
        Assert.Contains($"collaboration={session.Id}", collaboration.InvestigateUri);
        Assert.Contains($"perspective={session.InitiatorOrganizationUserId}", collaboration.InvestigateUri);
        f.Ceo.PermissionLevel = OrganizationPermissionLevel.Contributor; await f.Db.SaveChangesAsync();
        Assert.False(Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default)).Collaboration!.CanRetry);
        f.Ceo.PermissionLevel = OrganizationPermissionLevel.Owner;
        session.Status = CSweet.Domain.Communications.AgentCoordinationStatus.Active; await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        session.Status = CSweet.Domain.Communications.AgentCoordinationStatus.Failed; await f.Db.SaveChangesAsync();
        var content = Assert.Single(f.Db.CoreConversationMessages).Content;
        Assert.True(content.Length < 1000);
        Assert.Contains("Retry collaboration", content); Assert.Contains(session.ConversationId.ToString(), content);
        Assert.Contains("1 execution attempt", content); Assert.DoesNotContain("model.response.chunk", content);
        Assert.DoesNotContain("outside", content); Assert.DoesNotContain("Observed facts", content);
        // Another source snapshot changes the diagnostic revision, not the human report.
        session.UpdatedAt = f.Clock.Now.AddMinutes(1); await f.Db.SaveChangesAsync(); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Single(f.Db.CoreConversationMessages); Assert.Single(f.Db.UserNotifications);
        session.Status = CSweet.Domain.Communications.AgentCoordinationStatus.Completed; session.CompletedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync(); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Equal(2, f.Db.CoreConversationMessages.Count());
        Assert.Contains(f.Db.CoreConversationMessages, x => x.Content.Contains("blocker cleared"));
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
    }

    [Fact]
    public async Task DiagnosticsPrioritizeFailuresInsteadOfHundredsOfModelChunks()
    {
        await using var f = await Fixture.Create(); await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = Guid.NewGuid() };
        var run = new AgentRunLog { Id = Guid.NewGuid(), OrganizationId = f.Org, AgentWorkAttemptId = attempt.Id,
            Model = "test-model", Status = "Failed", FailureMessage = "Provider connection interrupted", DurationMs = 1000 };
        incident.EvidenceJson = JsonSerializer.Serialize(new[] { new ProjectDiagnosticEvidence("AgentWorkAttempt", attempt.Id, f.Clock.Now, "execution failed") },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        f.Db.AddRange(attempt, run);
        f.Db.AuditEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, EntityType = "Capability", EntityId = attempt.Id,
            EventType = "agent.capability.failed", Outcome = "Failed", Sequence = 1, PayloadPreview = "Connection was interrupted" });
        for (var i = 2; i < 240; i++)
            f.Db.AuditEvents.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Org, EntityType = "AgentRunLog", EntityId = run.Id,
                EventType = "model.response.chunk", Outcome = "Running", Sequence = i, PayloadPreview = "raw model reasoning" });
        await f.Db.SaveChangesAsync();
        var diagnostics = await f.Service.ReadDiagnosticsAsync(f.Org, f.Producer.Id, new(incident.Id), default);
        Assert.Contains(diagnostics.Evidence, x => x.Kind == "ModelRequest" && x.Detail.Contains("Provider connection interrupted"));
        Assert.Contains(diagnostics.Evidence, x => x.Kind == "agent.capability.failed");
        Assert.DoesNotContain(diagnostics.Evidence, x => x.Kind == "model.response.chunk" || x.Detail.Contains("raw model reasoning"));
        Assert.Null(diagnostics.NextOffset);
    }

    [Theory]
    [InlineData(AgentWorkStatus.Completed)]
    [InlineData(AgentWorkStatus.Cancelled)]
    public async Task FinishedReviewerInboxWorkDoesNotResolveBlockedStageOrRepeatOwnerAlerts(AgentWorkStatus inboxStatus)
    {
        await using var f = await Fixture.Create();
        var ticket = f.Ticket(WorkTaskStatus.Blocked);
        var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id, WorkItem = ticket,
            CurrentStageKey = "quality", Status = WorkItemExecutionStatus.Blocked, CreatedAt = f.Clock.Now };
        var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id, ItemExecution = execution,
            StageKey = "quality", Status = WorkStageExecutionStatus.Blocked, CreatedAt = f.Clock.Now,
            LastError = "The author has not delivered an exact artifact revision." };
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = f.Org.ToString(),
            AgentInstallationId = f.Producer.AgentInstallationId!.Value, Name = "work.execution.run",
            SourceType = "WorkStageExecution", SourceId = stage.Id.ToString(), Status = inboxStatus, CreatedAt = f.Clock.Now };
        f.Db.AddRange(ticket, execution, stage, work, new WorkExecutionAttempt { Id = Guid.NewGuid(),
            StageExecutionId = stage.Id, AgentWorkItemId = work.Id, Status = WorkExecutionAttemptStatus.Completed });
        await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        var deadline = incident.EscalateAt;
        var observation = await new ProjectHealthReader(f.Db, f.Clock).ObserveAsync(f.Project, default);
        Assert.DoesNotContain(incident.Fingerprint, observation.Recovered);
        for (var i = 0; i < 3; i++)
        {
            f.Clock.Now += TimeSpan.FromMinutes(1); await f.Review();
            Assert.Equal("Open", incident.Status); Assert.Single(f.Db.ProjectIncidents);
            Assert.Equal(deadline, incident.EscalateAt);
        }
        foreach (var actor in new[] { f.Producer.Id, f.Director.Id, f.Chief.Id })
            await f.Service.ForwardAsync(f.Org, actor, new(incident.Id, incident.Revision, $"forward:{actor}", "Review needed"), default);
        await RecordOwnerRequirement(f, incident);
        await f.Service.DeliverAsync(default);
        var alert = Assert.Single(f.Db.UserNotifications);
        Assert.Equal(CSweet.Domain.Communications.NotificationSeverity.Important, alert.Severity);
        Assert.Contains("exact artifact revision", alert.Body);
        Assert.Contains("deliver the exact document revision", Assert.Single(f.Db.CoreConversationMessages).Content);
        for (var i = 0; i < 5; i++)
        { f.Clock.Now += TimeSpan.FromMinutes(1); await f.Review(); await f.Service.DeliverAsync(default); }
        Assert.Equal("Open", incident.Status); Assert.Single(f.Db.ProjectIncidents);
        Assert.Single(f.Db.CoreConversationMessages); Assert.Single(f.Db.UserNotifications);

        stage.Status = WorkStageExecutionStatus.Completed;
        ticket.Status = WorkTaskStatus.Completed;
        await f.Db.SaveChangesAsync(); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Equal("Resolved", incident.Status); Assert.Single(f.Db.ProjectIncidents);
        Assert.NotNull(alert.DismissedAt);
        Assert.Equal(CSweet.Domain.Communications.NotificationSeverity.Routine,
            f.Db.UserNotifications.Single(x => x.Id != alert.Id).Severity);
        Assert.Equal(2, f.Db.CoreConversationMessages.Count());
        Assert.Contains(f.Db.ApplicationRealtimeOutbox, x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.NotificationUpdated);
    }

    [Fact]
    public async Task RecoveryBeforeOwnerDeliveryDoesNotSendAnUnseenBlockerClearedAlert()
    {
        await using var f = await Fixture.Create();
        var ticket = await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        foreach (var actor in new[] { f.Producer.Id, f.Director.Id, f.Chief.Id })
            await f.Service.ForwardAsync(f.Org, actor, new(incident.Id, incident.Revision, $"forward:{actor}", "Review needed"), default);
        ticket.Status = WorkTaskStatus.Completed;
        await f.Db.SaveChangesAsync(); await f.Review(); await f.Service.DeliverAsync(default);
        Assert.Equal("Resolved", incident.Status);
        Assert.Empty(f.Db.CoreConversationMessages); Assert.Empty(f.Db.UserNotifications);
        Assert.All(f.Db.ProjectIncidentDeliveries, x => Assert.NotNull(x.DeliveredAt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerReportAndOverviewOpenTheSameExactBlockedItem(bool personal)
    {
        await using var f = await Fixture.Create();
        if (personal)
        {
            f.Board.Kind = WorkBoardKind.Personal;
            f.Board.OwnerOrganizationUserId = f.Producer.Id;
            f.Board.WorkstreamId = null;
        }
        var ticket = f.Ticket(WorkTaskStatus.Blocked);
        ticket.Title = "Reconcile scope, technical backlog and delivery staffing";
        ticket.BlockReason = "Resolve the authority questions in the accepted brief.";
        ticket.PersonalWorkContextJson = JsonSerializer.Serialize(new { workstreamId = f.Project.Id });
        f.Db.Add(ticket); await f.Db.SaveChangesAsync(); await f.Review();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        foreach (var actor in new[] { f.Producer.Id, f.Director.Id, f.Chief.Id })
            await f.Service.ForwardAsync(f.Org, actor, new(incident.Id, incident.Revision, $"forward:{actor}", "Review needed"), default);
        await RecordOwnerRequirement(f, incident);
        await f.Service.DeliverAsync(default);

        var expected = personal
            ? $"/organizations/{f.Org}/employees/{f.Producer.Id}?tab=personal-board&item={ticket.Id}"
            : $"/organizations/{f.Org}/projects/boards/{f.Board.Id}?item={ticket.Id}";
        var attention = Assert.Single(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        Assert.Equal(expected, attention.InvestigateUri);
        Assert.Equal("Open blocked item", attention.InvestigateLabel);
        Assert.Contains($"[Open blocked item]({expected})", Assert.Single(f.Db.CoreConversationMessages).Content);
        Assert.Contains("Open the affected item", attention.RecommendedAction);
        Assert.EndsWith($"/communications/{f.Db.CoreConversations.Single().Id}", attention.ActionUri);

        // Read current membership rather than the original owner/board captured by the alert.
        var transferredBoard = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = f.Org,
            WorkstreamId = f.Project.Id, Key = "transferred", CreatedAt = f.Clock.Now };
        f.Db.Add(transferredBoard); ticket.BoardId = transferredBoard.Id; ticket.Status = WorkTaskStatus.Ready;
        await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Service.ReadAttentionAsync(f.Org, f.Ceo.Id, default));
        var currentDestination = await f.Service.PresentAsync(incident, default);
        Assert.Equal($"/organizations/{f.Org}/projects/boards/{transferredBoard.Id}?item={ticket.Id}", currentDestination.Link);
        Assert.Equal("Open affected item", currentDestination.LinkLabel);
    }

    [Theory]
    [InlineData("missing-item")]
    [InlineData("foreign-item")]
    [InlineData("other-project")]
    [InlineData("missing-board")]
    [InlineData("foreign-board")]
    [InlineData("archived-board")]
    [InlineData("missing-owner")]
    public async Task UnresolvableAffectedItemFallsBackWithoutLinkingUnrelatedWork(string scenario)
    {
        await using var f = await Fixture.Create(); var ticket = await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents);
        switch (scenario)
        {
            case "missing-item": f.Db.Remove(ticket); break;
            case "foreign-item": ticket.OrganizationId = Guid.NewGuid(); break;
            case "other-project": f.Board.WorkstreamId = Guid.NewGuid(); break;
            case "missing-board": ticket.BoardId = Guid.NewGuid(); break;
            case "foreign-board": f.Board.OrganizationId = Guid.NewGuid(); break;
            case "archived-board": f.Board.ArchivedAt = f.Clock.Now; break;
            case "missing-owner":
                f.Board.Kind = WorkBoardKind.Personal; f.Board.OwnerOrganizationUserId = Guid.NewGuid(); break;
        }
        await f.Db.SaveChangesAsync();
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.Equal($"/organizations/{f.Org}/projects/{f.Project.Id}", presentation.Link);
        Assert.Equal("Open project", presentation.LinkLabel);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("dependency-wait")]
    [InlineData("stage")]
    public async Task LegacyIncidentSourcesResolveTheExactItemWithoutAnAffectedItemField(string source)
    {
        await using var f = await Fixture.Create(); var ticket = await f.FailTicket();
        var incident = Assert.Single(f.Db.ProjectIncidents); incident.AffectedWorkItemId = null;
        var sourceId = ticket.Id;
        if (source == "stage")
        {
            var execution = new WorkItemExecution { Id = Guid.NewGuid(), WorkItemId = ticket.Id,
                CreatedAt = f.Clock.Now, Status = WorkItemExecutionStatus.Blocked };
            var stage = new WorkStageExecution { Id = Guid.NewGuid(), ItemExecutionId = execution.Id,
                StageKey = "quality", CreatedAt = f.Clock.Now, Status = WorkStageExecutionStatus.Blocked };
            f.Db.AddRange(execution, stage); sourceId = stage.Id;
        }
        incident.Fingerprint = $"{source}:{sourceId:N}"; await f.Db.SaveChangesAsync();
        var presentation = await f.Service.PresentAsync(incident, default);
        Assert.Equal($"/organizations/{f.Org}/projects/boards/{f.Board.Id}?item={ticket.Id}", presentation.Link);
        Assert.Equal("Open blocked item", presentation.LinkLabel);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Clock Clock { get; } = new();
        public CSweetDbContext Db { get; }
        public ProjectHealthService Service { get; }
        public Guid Org { get; } = Guid.NewGuid();
        public Workstream Project { get; }
        public WorkBoard Board { get; }
        public OrganizationUser Producer { get; }
        public OrganizationUser Director { get; }
        public OrganizationUser Chief { get; }
        public OrganizationUser Ceo { get; }
        private Fixture()
        {
            Db = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
                new EphemeralDataProtectionProvider(), Clock);
            Service = new(Db, new(Db, Clock), Clock);
            Ceo = Person("CEO", EmployeeType.Human); Ceo.PermissionLevel = OrganizationPermissionLevel.Owner;
            Chief = Person("Chief"); Chief.ReportsToOrganizationUserId = Ceo.Id;
            Director = Person("Director"); Director.ReportsToOrganizationUserId = Chief.Id;
            Producer = Person("Producer"); Producer.ReportsToOrganizationUserId = Director.Id;
            Producer.AgentInstallation = new() { Id = Producer.AgentInstallationId!.Value, BusinessId = Org.ToString(),
                PackageVersion = new() { Id = Guid.NewGuid(), ManifestJson = """{"rolePolicy":{"baseType":"manager","profile":"manager.v1","declaredRoleKeys":["manager","game-producer"]}}""" } };
            Project = new() { Id = Guid.NewGuid(), OrganizationId = Org, Name = "Test project", Status = WorkstreamStatus.Active,
                AccountableManagerOrganizationUserId = Producer.Id, CreatedAt = Clock.Now, UpdatedAt = Clock.Now };
            Board = new() { Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Project.Id, ManagerOrganizationUserId = Producer.Id, Key = "test", CreatedAt = Clock.Now };
            Db.AddRange(Ceo, Chief, Director, Producer, Project, Board, new WorkstreamSupervisionAssignment {
                Id = Guid.NewGuid(), OrganizationId = Org, WorkstreamId = Project.Id, SupervisorOrganizationUserId = Producer.Id, RoleKey = "game-producer", StartsAt = Clock.Now });
        }
        private OrganizationUser Person(string name, EmployeeType type = EmployeeType.Agent) => new() {
            Id = Guid.NewGuid(), OrganizationId = Org, DisplayName = name, EmployeeType = type,
            AgentInstallationId = type == EmployeeType.Agent ? Guid.NewGuid() : null, IsActive = true, CreatedAt = Clock.Now };
        public static async Task<Fixture> Create() { var f = new Fixture(); await f.Db.SaveChangesAsync(); return f; }
        public WorkTask Ticket(WorkTaskStatus status) => new() { Id = Guid.NewGuid(), OrganizationId = Org, BoardId = Board.Id,
            Title = "Work", Status = status, CreatedAt = Clock.Now, UpdatedAt = Clock.Now, BlockReason = status == WorkTaskStatus.Failed ? "agent.payload_invalid: schema error" : null };
        public async Task<WorkTask> FailTicket() { var task = Ticket(WorkTaskStatus.Failed); Db.Add(task); await Db.SaveChangesAsync(); await Review(); return task; }
        public async Task Review()
        {
            foreach (var state in Db.ProjectHealthStates) state.NextReviewAt = Clock.Now;
            await Db.SaveChangesAsync(); await Service.ReviewDueAsync(default);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
