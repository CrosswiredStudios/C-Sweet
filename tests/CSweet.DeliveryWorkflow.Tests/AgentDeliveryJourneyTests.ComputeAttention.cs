using System.IO.Compression;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.Agent.Testing;
using CSweet.Compute.Contracts;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.UI.Services;
using CSweet.UnitTests;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using QaAgent = CSweet.Agent.QA.VideoGame.SpecialistAgent;

namespace CSweet.DeliveryWorkflow.Tests;

public sealed partial class AgentDeliveryJourneyTests
{
    [Theory, InlineData(false), InlineData(true), Trait("Category", "AgentHarness")]
    public async Task QaComputeWaitShowsOneNamedOwnerCardUntilBrowserWorkspaceIsReady(bool legacy)
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        var owner = Guid.NewGuid();
        var ownerEmployee = Guid.NewGuid();
        var contributor = Guid.NewGuid();
        var qaEmployee = await f.Db.CoreOrganizationUsers.SingleAsync();
        qaEmployee.DisplayName = "Rohan Desai";
        f.Db.CoreOrganizationUsers.Add(new OrganizationUser { Id = ownerEmployee, ApplicationUserId = owner,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true, OrganizationId = f.Organization });
        f.Db.CoreOrganizationUsers.Add(new OrganizationUser { Id = contributor, ApplicationUserId = Guid.NewGuid(),
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true, OrganizationId = f.Organization });
        var project = await f.Db.Workstreams.SingleAsync();
        project.Status = WorkstreamStatus.Active;
        project.Name = "Prism Breaker";
        if (legacy)
            f.Db.Add(new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = f.Organization, State = "Failed",
                ErrorCode = "compute_checks_build_failed", PreparationAttemptId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow });
        await f.Db.SaveChangesAsync();
        var changes = f.Db.ApplicationRealtimeOutbox.Where(x => x.OrganizationId == f.Organization && x.EventType == AppRealtimeEvents.ComputeChanged);
        Assert.Empty((await DeliveryWorkflowBridge.ReadOverviewAsync(f.Db, f.Organization, ownerEmployee, legacy)).UserActions);

        var package = Guid.NewGuid();
        var members = new[] { new ArtifactPackageMemberDigest(Guid.NewGuid(), Guid.NewGuid(), "brief", Hash("brief")) };
        var planning = new WorkItemPlanningSpecification([], ["The startup page displays the canvas."]) {
            Verifications = [new("browser-smoke") { RequireCanvas = true }],
            ArtifactPackageDigest = new(package, 1, ArtifactPackageDigestCalculator.Calculate(package, 1, members), DateTimeOffset.UtcNow, members) };
        var input = new WorkExecutionInputV1(f.Workstream, Guid.NewGuid(), 1, planning) {
            AssignmentRequirements = new("game-quality-assurance", [], [], [WorkManagementCapabilityNames.ExecutionRun]),
            AssignmentSelection = new(f.Installation, 1, Hash("profile"), [], Hash("selection"), DateTimeOffset.UtcNow) };
        var delivery = new WorkItemDeliverySpecification(Guid.NewGuid(), [], planning.AcceptanceCriteria) {
            BaseBranch = "main", DeliveryPlanId = Guid.NewGuid() };
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with { Id = Guid.NewGuid(), PlanningRevision = 1,
            Planning = planning, Delivery = delivery, StageAssignments = [new("quality", "AgentInstallation", AgentInstallationId: f.Installation)] };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {
            Scope = "Task", OrganizationId = f.Organization, WorkstreamId = f.Workstream, ItemId = item.Id,
            BoardId = Guid.NewGuid(), SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(), StageExecutionId = Guid.NewGuid(), StageKey = "quality", DeliveryPlanId = delivery.DeliveryPlanId,
            AssignmentRevision = 1, PlanningRevision = 1, AgentInstallationId = f.Installation, Deadline = DateTimeOffset.UtcNow.AddHours(1),
            Item = Element(new { id = item.Id, deliverySpecificationJson = JsonSerializer.Serialize(delivery, Json) }),
            Input = Element(input), Evidence = [], PriorOutcomes = [] };
        f.Db.WorkBoards.Add(new() { Id = assignment.BoardId, OrganizationId = f.Organization, WorkstreamId = f.Workstream,
            Name = "Browser QA" });
        f.Db.CoreWorkTasks.Add(new() { Id = item.Id, BoardId = assignment.BoardId, OrganizationId = f.Organization,
            Title = "Check the canvas", Status = WorkTaskStatus.Running });
        f.Db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            SubjectKind = GrantSubjectKind.OrganizationUser, SubjectId = ownerEmployee, ScopeKind = GrantScopeKind.Board,
            ScopeId = assignment.BoardId, Action = CSweet.Contracts.WorkManagement.WorkBoardActions.Read });
        await f.Db.SaveChangesAsync();
        var workspace = Guid.NewGuid();
        using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("index.html").Open()))
            writer.Write("<canvas></canvas>");
        var defaults = new ComputeDefaultsService(f.Db, TimeProvider.System,
            new ComputeGrantAdministration(f.Db, TimeProvider.System, new AuditExecutionContextAccessor()), preparation: new(legacy));
        var runtime = StateRuntime()
            .RegisterCapability<WorkItemReference, WorkItem>(WorkManagementCapabilityNames.ItemRead, (_, _) => Task.FromResult(item))
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare, (_, _) =>
                Task.FromResult(new GitWorkspaceResult(workspace, item.Id, "/remote/qa", delivery.RepositoryId, "InternalGit", "PullRequest", new('a', 40), "Ready", false)))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability, (_, _) =>
                Task.FromResult(new GitWorkspaceSyncResult(source.ToArray())))
            .RegisterCapability<JsonElement, CSweet.Application.Compute.ComputeDefaults>(ComputeCapabilities.Read, (r, ct) =>
                defaults.ReadProjectAsync(f.Organization, f.Installation, r.GetProperty("workstreamId").GetGuid(), ct));

        var previous = SetWorkspaceLimits();
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var host = new AgentTestHost(new QaAgent(), runtime, businessId: f.Organization.ToString("D"), installationId: f.Installation.ToString("D"));
                assignment = assignment with { AttemptId = Guid.NewGuid(), Attempt = attempt };
                var reply = await host.ExecuteCapabilityAsync(WorkManagementCapabilityNames.ExecutionRun, assignment);
                Assert.True(reply.Succeeded, reply.Error);
                var outcome = reply.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Json)!;
                Assert.True(outcome.OutcomeCode == "awaiting-compute-preparation", outcome.Summary);
                Assert.Contains(legacy ? "compute_checks_build_failed" : OfficeComputeCapacityRequest.WaitingReason, outcome.Summary);
                await using var db = new CSweetDbContext(f.Options);
                await DeliveryWorkflowBridge.AcceptQaWaitAsync(db, assignment, outcome);
                Assert.Empty(await db.Set<OfficeComputeCapacityRequest>().ToListAsync());
                var overview = await DeliveryWorkflowBridge.ReadOverviewAsync(db, f.Organization, ownerEmployee, legacy);
                var card = Assert.Single(overview.UserActions);
                Assert.True(card.IsComputeBlocker);
                Assert.Equal("Rohan Desai", card.OwnerName);
                Assert.Equal(qaEmployee.Id, card.OwnerOrganizationUserId);
                Assert.Equal("Prism Breaker", card.ProjectName);
                Assert.Equal("Waiting for a browser workspace", card.Title);
                Assert.Contains("I need a Linux workspace to run the browser tests.", card.Summary);
                Assert.Equal($"/organizations/{f.Organization:D}/compute", Assert.Single(card.Actions).ActionUri);
                Assert.Equal(item.Id, Assert.Single(card.AffectedWork).ItemId);
                var dashboard = await DeliveryWorkflowBridge.ReadComputeAsync(db, f.Organization, owner, legacy);
                Assert.Equal(1, OverviewAttentionItems.Count(null, null, null, dashboard, issues: overview));
                Assert.Empty((await DeliveryWorkflowBridge.ReadOverviewAsync(db, f.Organization, contributor, legacy)).UserActions);
            }
            await using var recovered = new CSweetDbContext(f.Options);
            if (!legacy)
            {
                var demand = await defaults.RequestProjectWorkspaceAsync(f.Organization, f.Installation, f.Workstream, default);
                Assert.Equal(OfficeComputeCapacityRequest.WaitingReason, demand.ErrorCode);
                Assert.Single(await recovered.Set<OfficeComputeCapacityRequest>().ToListAsync());
                Assert.Single((await DeliveryWorkflowBridge.ReadOverviewAsync(recovered, f.Organization, ownerEmployee, legacy)).UserActions);
            }
            var digest = "sha256:" + new string('a', 64);
            var templateId = OfficeComputePlacement.TemplateId(digest);
            var definition = new ComputeTemplate(templateId, "linux", "x64", digest, ["docker"]);
            var registration = new ComputeTemplateRegistration { Id = Guid.NewGuid(), OrganizationId = f.Organization,
                TemplateId = templateId, TemplateJson = JsonSerializer.Serialize(definition, ComputeProtocol.Json), Enabled = true };
            recovered.ComputeTemplates.Add(registration);
            if (legacy)
            {
                var setup = await recovered.Set<ComputeLocalSetup>().SingleAsync();
                setup.State = "Ready";
                setup.TemplateId = templateId;
                setup.ErrorCode = null;
            }
            else
            {
                var pool = new ExecutionPool { Id = Guid.NewGuid(), IsEnabled = true };
                var node = new ExecutionNode { Id = Guid.NewGuid(), ExecutionPool = pool, Status = ExecutionNodeStatus.Ready,
                    ApprovedAt = DateTimeOffset.UtcNow, LastHeartbeatAt = DateTimeOffset.UtcNow,
                    CertificateExpiresAt = DateTimeOffset.UtcNow.AddHours(1), ComputeTemplateId = templateId,
                    ComputeCapacityJson = """{"slots":1,"resources":{"cpuCount":2,"memoryMiB":2048,"diskMiB":20480}}""" };
                recovered.ExecutionNodes.Add(node);
                recovered.ComputeNodes.Add(new() { Id = node.Id, OrganizationId = f.Organization,
                    ProviderId = OfficeComputePlacement.ProviderId, Enabled = true });
                recovered.ComputeTemplatePlacements.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization,
                    NodeId = node.Id, TemplateRegistrationId = registration.Id, Enabled = true });
            }
            await recovered.SaveChangesAsync();
            Assert.Single((await DeliveryWorkflowBridge.ReadOverviewAsync(recovered, f.Organization, ownerEmployee, legacy)).UserActions);
            var earlierWakes = await changes.Select(e => e.Id).ToArrayAsync();
            registration.TemplateJson = JsonSerializer.Serialize(definition with { Features = ["docker", "browser-testing-v1"] }, ComputeProtocol.Json);
            await recovered.SaveChangesAsync();
            Assert.Empty((await DeliveryWorkflowBridge.ReadOverviewAsync(recovered, f.Organization, ownerEmployee, legacy)).UserActions);
            var wake = await changes.SingleAsync(e => !earlierWakes.Contains(e.Id));
            Assert.Contains(ownerEmployee, JsonSerializer.Deserialize<Guid[]>(wake.RecipientOrganizationUserIdsJson)!);
            Assert.Equal(WorkStageExecutionStatus.Backoff, (await f.Db.WorkStageExecutions.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(item.Id, (await f.Db.WorkItemExecutions.AsNoTracking().SingleAsync()).WorkItemId);
            Assert.Equal(2, await f.Db.WorkOrchestrationEvents.CountAsync(x => x.EventType == "qa.compute-preparation.waiting"));
        }
        finally
        {
            foreach (var limit in previous) Environment.SetEnvironmentVariable(limit.Key, limit.Value);
            var path = Path.GetFullPath(PlatformGitWorkspaceClient.LocalWorkspacePath(workspace));
            if (Path.GetDirectoryName(path) == Path.GetFullPath(PlatformGitWorkspaceClient.LocalWorkspaceRoot) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
