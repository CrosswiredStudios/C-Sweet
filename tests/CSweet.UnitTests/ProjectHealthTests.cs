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

public sealed class ProjectHealthTests
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
            Name = "work.execution.run.v1", Status = AgentWorkStatus.Pending, CreatedAt = f.Clock.Now, DeadlineAt = f.Clock.Now.AddHours(1) };
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
            Name = "work.execution.run.v1", Status = AgentWorkStatus.DeadLetter, LastError = "schema validation failed", CreatedAt = f.Clock.Now };
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
        await f.Service.DeliverAsync(default); await f.Service.DeliverAsync(default);
        Assert.Single(f.Db.CoreConversationMessages); Assert.Single(f.Db.UserNotifications);
        Assert.Contains(incident.Id.ToString(), f.Db.CoreConversationMessages.Single().Content);
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
    public async Task RunningLabelDoesNotSuppressStallButLiveAttemptDoes()
    {
        await using var f = await Fixture.Create();
        var ticket = f.Ticket(WorkTaskStatus.Running); f.Db.Add(ticket); await f.Db.SaveChangesAsync();
        await f.Review(); f.Clock.Now += TimeSpan.FromMinutes(16); await f.Review();
        Assert.Single(f.Db.ProjectIncidents);
        await using var live = await Fixture.Create();
        var task = live.Ticket(WorkTaskStatus.Running); live.Db.Add(task);
        var work = new AgentWorkItem { Id = Guid.NewGuid(), OrganizationId = live.Org.ToString(), AgentInstallationId = live.Producer.AgentInstallationId!.Value,
            Name = "work.execution.run.v1", Status = AgentWorkStatus.Leased, AttemptCount = 1, CreatedAt = live.Clock.Now, DeadlineAt = live.Clock.Now.AddHours(1) };
        var attempt = new AgentWorkAttempt { Id = Guid.NewGuid(), AgentWorkItemId = work.Id, Attempt = 1, ClaimedAt = live.Clock.Now, LeaseExpiresAt = live.Clock.Now.AddHours(1) };
        live.Db.AddRange(work, attempt, new CSweet.Domain.Analytics.WorkExecutionContext { Id = attempt.Id, OrganizationId = live.Org,
            AgentInstallationId = work.AgentInstallationId, AgentWorkItemId = work.Id, RootWorkItemId = task.Id });
        await live.Db.SaveChangesAsync(); await live.Review(); live.Clock.Now += TimeSpan.FromMinutes(16); await live.Review();
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

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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
