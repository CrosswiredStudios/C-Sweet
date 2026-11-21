using System.IO.Compression;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.Agent.Testing;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Compute;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
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
    [Fact, Trait("Category", "AgentHarness")]
    public async Task FailedLinuxPreparationKeepsQaWaitingAndShowsOneOwnerCardAcrossRestart()
    {
        await using var f = new ComputeBrokerTests.Fixture();
        await f.SeedAsync();
        var owner = Guid.NewGuid();
        f.Db.CoreOrganizationUsers.Add(new OrganizationUser { Id = Guid.NewGuid(), ApplicationUserId = owner,
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true, OrganizationId = f.Organization });
        (await f.Db.Workstreams.SingleAsync()).Status = WorkstreamStatus.Active;
        f.Db.Add(new ComputeLocalSetup { Id = Guid.NewGuid(), OrganizationId = f.Organization, State = "Failed",
            ErrorCode = "compute_checks_build_failed", PreparationAttemptId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow });
        await f.Db.SaveChangesAsync();
        var changes = f.Db.ApplicationRealtimeOutbox.Where(x => x.OrganizationId == f.Organization && x.EventType == AppRealtimeEvents.ComputeChanged);
        var initialEvents = await changes.CountAsync();

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
        var workspace = Guid.NewGuid();
        using var source = new MemoryStream();
        using (var archive = new ZipArchive(source, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("index.html").Open()))
            writer.Write("<canvas></canvas>");
        var defaults = new ComputeDefaultsService(f.Db, TimeProvider.System,
            new ComputeGrantAdministration(f.Db, TimeProvider.System, new AuditExecutionContextAccessor()));
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
                Assert.Contains("compute_checks_build_failed", outcome.Summary);
                await using var db = new CSweetDbContext(f.Options);
                await DeliveryWorkflowBridge.AcceptQaWaitAsync(db, assignment, outcome);
                var dashboard = await DeliveryWorkflowBridge.ReadComputeAsync(db, f.Organization, owner);
                Assert.Equal(1, OverviewAttentionItems.Count(null, null, null, dashboard));
                Assert.Equal("compute_checks_build_failed", OverviewAttentionItems.FailedComputePreparation(dashboard)?.FailureCode);
            }
            Assert.Equal(initialEvents, await changes.CountAsync());
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
