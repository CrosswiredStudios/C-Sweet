using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Api.Compute;
using CSweet.Contracts.Compute;
using CSweet.Contracts.Core;
using CSweet.Contracts.Setup;
using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Compute;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public static class DeliveryWorkflowBridge
{
    internal static Task<ComputeDashboard?> ReadComputeAsync(CSweetDbContext db, Guid business, Guid user, bool legacy)
        => ComputeDashboardEndpoints.ReadAsync(db, business, user, Path.GetTempPath(), Path.GetTempPath(), default,
            legacyLocalSetupEnabled: legacy, details: true);

    internal static Task<bool> RecheckComputeAsync(CSweetDbContext db, Guid business, Guid user, Guid stage)
        => ComputeManagementEndpoints.RecheckAsync(db, business, user, stage, false, TimeProvider.System, default);

    internal static Task<bool> RecheckProvisioningAsync(CSweetDbContext db, Guid business, Guid user, Guid environment, Guid operation)
        => ComputeManagementEndpoints.RecheckProvisioningAsync(db, business, user, environment, operation, TimeProvider.System, default);

    internal static async Task<Guid> SeedRetiredOfficeAsync(CSweetDbContext db, Guid user)
    {
        var old = new Organization { Id = Guid.NewGuid(), Name = "Deleted studio" };
        var pool = new ExecutionPool { Id = Guid.NewGuid(), IsEnabled = true, Name = "Default" };
        var office = new ExecutionNode { Id = Guid.NewGuid(), Name = "Office", MachineName = "Office", ExecutionPool = pool,
            OperatingSystem = "windows", Architecture = "x64", NodeVersion = "0.19.2", Status = ExecutionNodeStatus.Ready,
            ApprovedAt = DateTimeOffset.UtcNow, CertificateExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastHeartbeatAt = DateTimeOffset.UtcNow,
            AllocatableCpuCount = 4, AllocatableMemoryMb = 4096, AllocatableDiskMb = 40960, MaximumConcurrentWorkloads = 2,
            ComputeTemplateId = OfficeComputePlacement.TemplateId("sha256:" + new string('b', 64)),
            ComputeCapacityJson = """{"slots":4,"resources":{"cpuCount":4,"memoryMiB":4096,"diskMiB":40960}}""" };
        db.CoreOrganizations.Add(old);
        db.ExecutionNodes.Add(office);
        db.LocalOfficeSetupSessions.Add(new() { Id = Guid.NewGuid(), CreatedByUserId = user, ExecutionNodeId = office.Id,
            Status = LocalOfficeSetupSessionStatus.Connected, ComputeReservationBusinessId = old.Id,
            ComputeReservationImageDigest = "sha256:" + new string('b', 64), HandoffSecretHash = new string('a', 64),
            AllocatableCpuCount = 8, AllocatableMemoryMb = 8192, AllocatableDiskMb = 81920, MaximumConcurrentWorkloads = 6,
            WorkspaceBudgetJson = JsonSerializer.Serialize(new OfficeWorkspaceBudget(4, 4096, 40960)),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        await new OrganizationDataPurgeService(db, new EmptyBusiness(), NullLogger<OrganizationDataPurgeService>.Instance).PurgeAsync(old.Id);
        Assert.False(await db.CoreOrganizations.AnyAsync(o => o.Id == old.Id));
        Assert.Null(office.ComputeTemplateId);
        Assert.Equal(old.Id, (await db.LocalOfficeSetupSessions.SingleAsync()).ComputeReservationBusinessId);
        return office.Id;
    }

    internal static async Task RecoverRetiredOfficeAsync(CSweetDbContext db, Guid officeId, Guid business, Guid user, string digest)
    {
        await new SetupService(db).EnsureSeededAsync();
        var previous = (await db.LocalOfficeSetupSessions.SingleAsync(s => s.ExecutionNodeId == officeId)).ComputeReservationBusinessId;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CSweet:ExecutionGateway:PublicUrl"] = "https://office.example.test/",
            ["CSweet:ExecutionGateway:PublicCertificateSha256"] = new string('a', 64)
        }).Build();
        using var services = new ServiceCollection().AddSingleton(Options.Create(new OfficeComputeCatalogOptions
        {
            ApprovedImages = [new(business, digest, ["docker", "docker-apps-v1", ComputeBrowserTest.Feature])]
        })).BuildServiceProvider();
        var fleet = new ExecutionFleetService(db, new TestAuditEventWriter(), TimeProvider.System,
            Options.Create(new ExecutionFleetOptions { PublicLaunchEnabled = true }), appConfiguration: config, services: services);
        var office = await db.ExecutionNodes.SingleAsync(n => n.Id == officeId);
        var created = await fleet.CreateLocalSetupSessionAsync(new("custom", 0, 0, 0, officeId,
            Repair: true, RecoverComputeForBusinessId: business), user);
        Assert.True(created.Succeeded, created.Message);
        var session = Assert.IsType<LocalOfficeSetupSessionResponse>(created.Session);
        Assert.Contains("reassignCompute=true", session.LaunchUri);
        var launch = await fleet.LaunchLocalSetupSessionAsync(session.Id, user, new(session.LaunchUri!));
        Assert.True(launch.Succeeded, launch.Message);
        var handoff = new Uri(session.LaunchUri!).Fragment["#handoff=".Length..];
        var redeemed = await fleet.RedeemLocalSetupSessionAsync(new(handoff, office.MachineName, "windows", "x64", office.NodeVersion));
        Assert.True(redeemed.Succeeded, redeemed.Message);
        var setup = new OfficeComputeSetupRequest(session.Id, redeemed.SetupReceipt!, office.MachineName, "windows", "x64", office.NodeVersion, digest);
        Assert.Null(await fleet.PrepareHostedComputeAsync(setup with { SetupReceipt = "different" }, default));
        Assert.False((await fleet.PrepareHostedComputeAsync(setup with { ImageDigest = "sha256:" + new string('c',64) }, default))!.Enabled);
        var plan = await fleet.PrepareHostedComputeAsync(setup, default);
        Assert.Equal(new OfficeComputeSetupPlan(true, null, business, 4, 4, 4096, 40960, previous), plan);
        Assert.Equal(plan, await fleet.PrepareHostedComputeAsync(setup, default));
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Office", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256).CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        office.IssuedCertificateBase64 = Convert.ToBase64String(certificate.RawData);
        office.CertificateThumbprint = certificate.Thumbprint;
        var offer = new CSweet.Office.Contracts.ControlPlane.OfficeComputeOffer
        {
            OrganizationId = business.ToString("D"), TemplateId = OfficeComputePlacement.TemplateId(digest),
            ImageDigest = digest, MaximumEnvironments = 4, CpuCount = 4, MemoryMib = 4096, DiskMib = 40960
        };
        offer.Features.Add(["docker", "docker-apps-v1", ComputeBrowserTest.Feature]);
        await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, now, default);
        Assert.Null(office.ComputeTemplateId);
        Assert.Empty(await db.ComputeNodes.ToListAsync());
        var complete = new CSweet.Office.Contracts.ControlPlane.ReportAssistedOfficeSetupResultRequest(session.Id,
            redeemed.SetupReceipt!, "office_upgrade_completed", office.MachineName, "windows", "x64");
        Assert.True(await fleet.ReportLocalSetupResultAsync(complete));
        Assert.False(await fleet.ReportLocalSetupResultAsync(complete));
        await OfficeComputePlacement.ApplyOfferAsync(db, office, offer, now, default);
        office.DrainingAt = null;
        office.WorkPauseReason = null;
        office.Status = ExecutionNodeStatus.Ready;
        office.LastHeartbeatAt = now;
        await db.SaveChangesAsync();
        Assert.Equal(officeId, (await db.ComputeNodes.SingleAsync()).Id);
    }

    private sealed class EmptyBusiness : IBusinessAgentInstallationCleanup
    {
        public Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal static Task<OverviewIssueDirectory> ReadOverviewAsync(CSweetDbContext db, Guid business, Guid actor, bool legacy)
    {
        var clock = TimeProvider.System;
        var todos = new PersonalTodoService(db, clock);
        var authorization = new ScopedActionAuthorizationService(db);
        var issues = new WorkIssueService(db, authorization, clock);
        var blocked = new BlockedWorkAttentionReader(db, todos, clock, new(db, todos, authorization, issues));
        var reader = new OverviewIssueReader(db, todos, blocked, new ExecutiveDecisionService(db, new ChatTurnService(db)),
            new(db, new(db, clock), clock), issues, new(legacy), clock);
        return reader.ReadAsync(business, new(actor, null), false, default);
    }

    internal static async Task AcceptQaWaitAsync(CSweetDbContext db, WorkExecutionAssignment assignment, WorkExecutionOutcomeV1 outcome)
    {
        var stage = await db.WorkStageExecutions.Include(s => s.Attempts).Include(s => s.ItemExecution!)
            .ThenInclude(i => i.SprintExecution).SingleOrDefaultAsync(s => s.Id == assignment.StageExecutionId);
        if (stage is null)
        {
            var sprint = new WorkSprintExecution { Id = assignment.TaskSprintExecutionId, OrganizationId = assignment.OrganizationId,
                BoardId = assignment.BoardId, SprintId = assignment.TaskSprintId, PolicyRevisionId = assignment.TaskPolicyRevisionId };
            var item = new WorkItemExecution { Id = assignment.ExecutionId, SprintExecution = sprint, WorkItemId = assignment.TaskItemId,
                CurrentStageKey = assignment.StageKey };
            stage = new WorkStageExecution { Id = assignment.StageExecutionId, ItemExecution = item, StageKey = assignment.StageKey,
                AgentInstallationId = assignment.AgentInstallationId };
            db.Add(stage);
        }
        stage.Status = WorkStageExecutionStatus.Running;
        var attempt = new WorkExecutionAttempt { Id = assignment.AttemptId, StageExecutionId = stage.Id,
            Attempt = assignment.Attempt, Status = WorkExecutionAttemptStatus.Running };
        stage.Attempts.Add(attempt);
        db.Add(attempt);
        var orchestrator = new WorkOrchestrator(db, null!, null!, null!, [], TimeProvider.System, null!);
        Assert.Null(orchestrator.AcceptComputePreparationWait(stage.ItemExecution!.SprintExecution!, stage, attempt, outcome, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        Assert.Equal(0, WorkOrchestrator.RetryAttemptCount(stage));
    }

    public static void ValidateReview(ReviewGitMergeRequest request)
    {
        var tool = new McpToolCatalog([]).List(new HashSet<string> { GitMergeCapabilities.Review }).Single();
        var result = McpGatewayEndpoints.ValidateToolInput("workflow-review", JsonSerializer.SerializeToElement(request,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), tool.InputSchema);
        if (result is not null) throw new InvalidOperationException(result.Error);
    }
}
