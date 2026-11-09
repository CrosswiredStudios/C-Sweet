using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class ResourceFulfillmentTests
{
    [Fact]
    public async Task Fresh_request_manager_approval_restart_and_verification_resume_one_operation()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync();
        Assert.Equal("PendingApproval", request.Status);
        Assert.False(await f.Service.ProcessNextAsync(default));
        Assert.Equal(0, f.Handler.Installations);
        var replay = await f.RequestAsync();
        Assert.Equal(request.Id, replay.Id);
        Assert.Single(await f.Db.ActionProposals.ToArrayAsync());
        var decision = f.Approve(request);
        await f.Service.DecideAsync(f.Manager, decision, default);
        await f.Service.DecideAsync(f.Manager, decision, default);
        f.Handler.CrashAfterInstall = true;
        await f.Service.ProcessNextAsync(default);
        Assert.Equal("WaitingDependency", (await f.ReadAsync(request.Id)).Status);
        f.Clock.Advance(); f.Clock.Advance();
        await f.RestartAsync();
        await f.Service.ProcessNextAsync(default);
        Assert.Equal(1, f.Handler.Installations);
        Assert.Equal("Installing", (await f.ReadAsync(request.Id)).Status);
        f.Clock.Advance(); await f.Service.ProcessNextAsync(default);
        Assert.Equal("Certifying", (await f.ReadAsync(request.Id)).Status);
        Assert.DoesNotContain(await f.Db.AgentPlatformEventOutbox.ToArrayAsync(), x => x.DataJson.Contains("\"reason\":\"ready\""));
        f.Handler.Verified = true;
        f.Clock.Advance(); await f.RestartAsync(); await f.Service.ProcessNextAsync(default);
        var ready = await f.ReadAsync(request.Id);
        Assert.Equal("Ready", ready.Status);
        Assert.NotNull(ready.ProviderInstallationId); Assert.NotNull(ready.CertificationRunId);
        Assert.Equal(1, f.Handler.Installations);
        Assert.Single(await f.Db.ActionProposals.ToArrayAsync());
        Assert.Contains(await f.Db.AgentPlatformEventOutbox.ToArrayAsync(), x => x.DataJson.Contains("\"reason\":\"ready\""));
        Assert.Contains(await f.Db.ApplicationRealtimeOutbox.ToArrayAsync(), x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.ResourceChanged);
        // A repeated wake reads the same durable Ready request; it cannot reinstall.
        Assert.Equal(ready.Id, (await f.RequestAsync()).Id);
    }

    [Theory]
    [InlineData("stale-revision")] [InlineData("changed-plan")] [InlineData("wrong-manager")]
    [InlineData("changed-artifact")] [InlineData("revoked-manager")]
    public async Task Invalid_approval_never_authorizes_provisioning(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync(); var decision = f.Approve(request);
        var actor = f.Manager;
        switch (scenario)
        {
            case "stale-revision": decision = decision with { ExpectedRevision = 99 }; break;
            case "changed-plan": decision = decision with { PlanHash = "other" }; break;
            case "wrong-manager": actor = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Organization, IsActive = true }; break;
            case "changed-artifact": f.Handler.Valid = false; break;
            case "revoked-manager": actor.IsActive = false; await f.Db.SaveChangesAsync(); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.DecideAsync(actor, decision, default));
        Assert.Equal("PendingApproval", (await f.ReadAsync(request.Id)).Status);
        Assert.False(await f.Service.ProcessNextAsync(default)); Assert.Equal(0, f.Handler.Installations);
    }

    [Theory]
    [InlineData("failure")] [InlineData("revocation")] [InlineData("manager-change")]
    public async Task Setup_failure_and_authority_changes_remain_visible_without_false_readiness(string scenario)
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync(); await f.Service.DecideAsync(f.Manager, f.Approve(request), default);
        if (scenario == "revocation") f.Handler.Valid = false;
        if (scenario == "manager-change") { f.Requester.ReportsToOrganizationUserId = Guid.NewGuid(); await f.Db.SaveChangesAsync(); }
        if (scenario == "failure") f.Handler.Fail = true;
        await f.Service.ProcessNextAsync(default);
        var state = await f.ReadAsync(request.Id);
        Assert.Contains(state.Status, new[] { "Failed", "NeedsAttention" });
        Assert.NotNull(state.Condition);
        Assert.Null(state.CertificationRunId);
        Assert.False(await f.Service.ProcessNextAsync(default));
    }

    [Fact]
    public async Task Explicit_retry_keeps_approved_scope_and_installation_but_starts_a_new_verification_attempt()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync(); await f.Service.DecideAsync(f.Manager, f.Approve(request), default);
        f.Handler.Fail = true; await f.Service.ProcessNextAsync(default);
        var failed = await f.ReadAsync(request.Id);
        f.Handler.Fail = false;
        var retried = await f.Service.DecideAsync(f.Manager, new(failed.Id, failed.Revision, failed.PlanHash,
            "Retry", "The temporary fixture prerequisite is restored.", "retry-one"), default);
        Assert.Equal(2, retried.FulfillmentAttempt); Assert.Equal(request.PlanHash, retried.PlanHash);
        Assert.Equal(request.ApprovalId, retried.ApprovalId);
        await f.Service.ProcessNextAsync(default);
        Assert.Equal(1, f.Handler.Installations);
    }

    [Fact]
    public async Task Escalation_routes_on_the_current_reporting_chain_and_rebinds_the_same_plan()
    {
        await using var f = await Fixture.CreateAsync();
        var executive = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = f.Organization, ApplicationUserId = Guid.NewGuid(),
            IsActive = true, DisplayName = "Executive", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
        f.Manager.ReportsToOrganizationUserId = executive.Id; f.Db.Add(executive); await f.Db.SaveChangesAsync();
        var request = await f.RequestAsync();
        var escalated = await f.Service.DecideAsync(f.Manager, new(request.Id, request.Revision, request.PlanHash,
            "Escalate", "Review this at the next management level.", "escalate"), default);
        Assert.Equal(executive.Id, escalated.ApproverId); Assert.Equal(request.PlanHash, escalated.PlanHash);
        var approved = await f.Service.DecideAsync(executive, f.Approve(escalated), default);
        Assert.Equal("Approved", approved.Status);
    }

    [Fact]
    public async Task Scope_and_request_keys_are_authoritative_and_unknown_resources_cannot_receive_approvals()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestAsync(f.Organization, f.Installation,
            f.Input with { Purpose = "Different purpose" }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RequestAsync(f.Organization, f.Installation,
            f.Input with { WorkstreamId = Guid.NewGuid(), IdempotencyKey = "other" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.RequestAsync(f.Organization, f.Installation,
            f.Input with { ResourceType = "arbitrary-shell", IdempotencyKey = "unsupported" }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadAsync(Guid.NewGuid(), f.Installation, new(RequestId: request.Id), default));
        Assert.Single(await f.Db.ActionProposals.ToArrayAsync());
    }

    [Fact]
    public async Task Manager_discovery_filters_before_paging_and_returns_the_actual_pending_plan()
    {
        await using var f = await Fixture.CreateAsync();
        var request = await f.RequestAsync();
        var managerInstallation = Guid.NewGuid();
        f.Manager.EmployeeType = EmployeeType.Agent;
        f.Manager.AgentInstallationId = managerInstallation;
        f.Db.Add(new AgentInstallation { Id = managerInstallation, BusinessId = f.Organization.ToString("D"),
            IsEnabled = true, SetupState = PluginSetupState.Ready, RevisionStatus = PluginRevisionStatus.Active });
        for (var i = 0; i < 110; i++)
        {
            var unrelated = request with { Id = Guid.NewGuid(), ApproverId = Guid.NewGuid(), WorkstreamId = Guid.NewGuid() };
            f.Db.Add(new PluginOperationalState { Id = unrelated.Id, OrganizationId = f.Organization,
                AgentInstallationId = f.Installation, Kind = ResourceFulfillmentService.StateKind,
                ExternalKey = unrelated.Id.ToString("N"), UpdatedAt = request.UpdatedAt.AddMinutes(1),
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(unrelated, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });
        }
        await f.Db.SaveChangesAsync();
        var pending = await f.Service.ReadAsync(f.Organization, managerInstallation, new(PendingApprovalOnly: true), default);
        Assert.Equal(request.Id, Assert.Single(pending).Id);
        var scoped = await f.Service.ReadAsync(f.Organization, f.Installation, new(WorkstreamId: f.Project), default);
        Assert.Equal(request.Id, Assert.Single(scoped).Id);
    }

    [Fact]
    public async Task Agent_manager_approval_uses_current_grants_and_revocation_stops_fulfillment()
    {
        await using var f = await Fixture.CreateAsync();
        var managerInstallation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = f.Organization.ToString("D"),
            IsEnabled = true, SetupState = PluginSetupState.Ready, RevisionStatus = PluginRevisionStatus.Active,
            Grant = new() { Id = Guid.NewGuid(), RequiredCapabilitiesJson = System.Text.Json.JsonSerializer.Serialize(new[] { ResourceCapabilities.Read, ResourceCapabilities.Decide }) } };
        f.Manager.EmployeeType = EmployeeType.Agent; f.Manager.AgentInstallationId = managerInstallation.Id;
        f.Db.Add(managerInstallation); await f.Db.SaveChangesAsync();
        var request = await f.RequestAsync(); Assert.Equal(f.Manager.Id, request.ApproverId);
        var pending = await f.Service.ReadAsync(f.Organization, managerInstallation.Id, new(PendingApprovalOnly: true), default);
        Assert.Equal(request.Id, Assert.Single(pending).Id);
        await f.Service.DecideAsync(f.Manager, f.Approve(request), default);
        managerInstallation.Grant.RequiredCapabilitiesJson = "[]"; await f.Db.SaveChangesAsync();
        await f.Service.ProcessNextAsync(default);
        Assert.Equal("NeedsAttention", (await f.ReadAsync(request.Id)).Status);
        Assert.Equal(0, f.Handler.Installations);
    }

    [Fact]
    public async Task Missing_published_release_remains_visible_before_approval_and_recovers_after_restart()
    {
        await using var f = await Fixture.CreateAsync(); f.Handler.MissingRelease = true;
        var request = await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        Assert.Equal("Preparing", request.Status); Assert.Null(request.Plan); Assert.Empty(await f.Db.ActionProposals.ToListAsync());
        await f.Service.ProcessNextAsync(default);
        var blocked = await f.ReadAsync(request.Id);
        Assert.Equal("WaitingDependency", blocked.Status); Assert.Equal("resource.provider_release_unavailable", blocked.FailureCode);
        Assert.Contains("catalog read access", blocked.Condition); Assert.Null(blocked.Plan);
        Assert.Equal(blocked.Id, (await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default)).Id);
        Assert.Equal(0, f.Handler.Installations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DecideAsync(f.Manager, f.Approve(blocked), default));
        for (var i = 0; i < 7; i++) f.Clock.Advance();
        await f.RestartAsync(); f.Handler.MissingRelease = false;
        await f.Service.ProcessNextAsync(default);
        var prepared = await f.ReadAsync(request.Id);
        Assert.Equal("PendingApproval", prepared.Status); Assert.NotNull(prepared.Plan); Assert.Null(prepared.FailureCode);
        Assert.Single(await f.Db.ActionProposals.ToListAsync()); Assert.Equal(0, f.Handler.Installations);
        Assert.Contains(await f.Db.ApplicationRealtimeOutbox.ToListAsync(), x => x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.ResourceChanged);
    }

    [Fact]
    public async Task Durable_replacement_cancels_only_matching_pending_setup_question_and_linked_card()
    {
        await using var f = await Fixture.CreateAsync();
        WorkstreamDecisionRecord Decision(string status = "Pending", string recipe = "phaser.web-2d.v1", string rule = "unsupported-or-uncertified-toolchain") => new() {
            Id = Guid.NewGuid(), OrganizationId = f.Organization, WorkstreamId = f.Project, Status = status,
            TypeKey = "video-game.toolchain-selection.v1", AuthorityRuleKey = rule, TypeDataJson = System.Text.Json.JsonSerializer.Serialize(new { recipeKey = recipe }) };
        var obsolete = Decision(); var answered = Decision("Decided"); var otherRecipe = Decision(recipe: "godot.native"); var feasibility = Decision(rule: "technical-feasibility-blocker");
        var otherProject = Decision(); otherProject.WorkstreamId = Guid.NewGuid();
        var card = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization, Status = ExecutiveDecisionStatus.Pending,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(new { workstreamDecisionId = obsolete.Id }) };
        f.Db.AddRange(obsolete, answered, otherRecipe, feasibility, otherProject, card); await f.Db.SaveChangesAsync();
        var request = await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        Assert.Equal("Cancelled", obsolete.Status); Assert.Null(obsolete.SelectedOptionId);
        Assert.Contains(request.Id.ToString("D"), obsolete.Rationale); Assert.Contains("replacementResourceRequestId", obsolete.TypeDataJson);
        Assert.Equal(ExecutiveDecisionStatus.Cancelled, card.Status);
        Assert.Equal("Decided", answered.Status); Assert.Equal("Pending", otherRecipe.Status); Assert.Equal("Pending", feasibility.Status); Assert.Equal("Pending", otherProject.Status);
        await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        Assert.Equal(2, obsolete.Revision);
    }

    [Fact]
    public async Task Catalog_retry_after_is_retained_without_approvals_or_immediate_retry_loops()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.PreparationError = new CSweet.Application.Setup.AgentImportPreviewException("GitHub rate limited catalog access.") {
            IsDependencyFailure = true, RetryAt = f.Clock.GetUtcNow().AddMinutes(5) };
        var request = await f.Service.RequestAsync(f.Organization, f.Installation, f.Input, default);
        await f.Service.ProcessNextAsync(default);
        var blocked = await f.ReadAsync(request.Id);
        Assert.Equal("WaitingDependency", blocked.Status); Assert.Equal("resource.catalog_access_unavailable", blocked.FailureCode);
        for (var i = 0; i < 7; i++) f.Clock.Advance();
        Assert.False(await f.Service.ProcessNextAsync(default));
        Assert.Empty(f.Db.ActionProposals); Assert.Equal(0, f.Handler.Installations);
        f.Handler.PreparationError = null;
        for (var i = 0; i < 30; i++) f.Clock.Advance();
        await f.Service.ProcessNextAsync(default);
        Assert.Equal("PendingApproval", (await f.ReadAsync(request.Id)).Status);
        Assert.Equal(0, f.Handler.Installations);
    }

    internal sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(10);
    }
    internal sealed class Handler : IResourceFulfillmentHandler
    {
        public string Key => "test-node";
        public bool Supports(string type, string profile) => type == ResourceCapabilities.BuildToolchain && profile == "phaser.web-2d.v1";
        public bool Valid = true, Verified, Fail, CrashAfterInstall, MissingRelease, InvalidPreparation;
        public Exception? PreparationError;
        public int Installations;
        private readonly Dictionary<Guid, Guid> _installed = [];
        public Task<ResourcePlan> PrepareAsync(Guid org, ResourceRequest input, CancellationToken ct) => PreparationError is { } error
            ? throw error
            : InvalidPreparation
            ? throw new InvalidOperationException("Provider package verification failed; review the published checksum.")
            : MissingRelease
            ? throw new ResourcePreparationDependencyException("resource.provider_release_unavailable", "Publish the provider bundle or configure catalog read access. Setup will retry.")
            : Task.FromResult(new ResourcePlan(
            Key, "Prepare Node build capacity", Guid.NewGuid(), "node", "1.0.2", "manifest", "package", Guid.NewGuid(), "definition",
            "linux", "image", [], [], [], 0, "USD"));
        public Task ValidateAsync(Guid org, ResourceRequestState input, CancellationToken ct) =>
            Valid ? Task.CompletedTask : throw new InvalidOperationException("Reviewed artifact changed or was revoked.");
        public Task<ResourceFulfillmentProgress> ReconcileAsync(Guid org, ResourceRequestState input, CancellationToken ct)
        {
            if (Fail) return Task.FromResult(new ResourceFulfillmentProgress("Failed", "Real fixture failed."));
            if (!_installed.TryGetValue(input.Id, out var id)) { _installed.Add(input.Id, id = Guid.NewGuid()); Installations++; }
            if (CrashAfterInstall) { CrashAfterInstall = false; throw new IOException("Lost installer response after persistence."); }
            return Task.FromResult(input.Status == "Approved" ? new("Installing", "Installed", id) :
                Verified ? new ResourceFulfillmentProgress("Ready", "Verified", id, input.Id) : new("Certifying", "Running fixtures", id));
        }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly string _database = Guid.NewGuid().ToString();
        public CSweetDbContext Db = null!;
        public ResourceFulfillmentService Service = null!;
        public readonly Handler Handler = new(); public readonly Clock Clock = new();
        public readonly Guid Organization = Guid.NewGuid(), Installation = Guid.NewGuid(), Project = Guid.NewGuid();
        public OrganizationUser Requester = null!, Manager = null!;
        public ResourceRequest Input => new(Project, ResourceCapabilities.BuildToolchain, "phaser.web-2d.v1", "Build the approved game", "game-build");
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); f.Open();
            f.Manager = new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, DisplayName = "Manager", IsActive = true, ApplicationUserId = Guid.NewGuid(),
                EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
            f.Requester = new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, DisplayName = "Technical Director", IsActive = true,
                EmployeeType = EmployeeType.Agent, AgentInstallationId = f.Installation, ReportsToOrganizationUserId = f.Manager.Id };
            f.Db.AddRange(f.Manager, f.Requester, new AgentInstallation { Id = f.Installation, BusinessId = f.Organization.ToString("D"),
                IsEnabled = true, SetupState = PluginSetupState.Ready, RevisionStatus = PluginRevisionStatus.Active },
                new Workstream { Id = f.Project, OrganizationId = f.Organization, Name = "Game", Status = WorkstreamStatus.Approved,
                    AccountableManagerOrganizationUserId = f.Requester.Id });
            await f.Db.SaveChangesAsync(); return f;
        }
        private void Open()
        {
            Db = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(_database).Options);
            Service = new(Db, [Handler], Clock);
        }
        public async Task RestartAsync() { await Db.DisposeAsync(); Open(); }
        public async Task<ResourceRequestState> RequestAsync()
        {
            var state = await Service.RequestAsync(Organization, Installation, Input, default);
            if (state.Status == "Preparing") { await Service.ProcessNextAsync(default); state = await ReadAsync(state.Id); }
            return state;
        }
        public Task<ResourceRequestState> ReadAsync(Guid id) => Service.GetAsync(Organization, id, default);
        public ResourceDecision Approve(ResourceRequestState state) => new(state.Id, state.Revision, state.PlanHash, "Approve", "Required for this project.", "approve");
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
