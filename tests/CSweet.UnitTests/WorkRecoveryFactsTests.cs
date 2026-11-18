using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.WorkManagement;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;
namespace CSweet.UnitTests;

public sealed partial class PersonalTodoServiceTests
{
    [Fact]
    public async Task Reporting_manager_sees_an_installed_package_change_for_the_current_unclassified_failure()
    {
        await using var db = CreateDb();
        var setup = Seed(db);
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), Version = "6.23.5", ImportedAt = DateTimeOffset.UtcNow.AddHours(-3) };
        var installation = new AgentInstallation { Id = setup.Agent.AgentInstallationId!.Value,
            BusinessId = setup.Organization.Id.ToString(), PackageVersionId = package.Id };
        db.AgentPackageVersions.Add(package);
        db.AgentInstallations.Add(installation);
        await db.SaveChangesAsync();
        var native = new PersonalTodoService(db, TimeProvider.System);
        var manager = new PersonalTodoActor(setup.FirstManager.Id, null);
        var todo = await native.AddAsync(setup.Organization.Id, manager, Add("Review returned Story", "returned-story", setup.Agent.Id));
        var task = await db.CoreWorkTasks.SingleAsync(t => t.Id == todo.Id);
        var failedAt = DateTimeOffset.UtcNow.AddHours(-1);
        for (int i = 0; i < 2; i++)
        {
            await AgentTicketFeedback.RecordFailureAsync(db, task, installation.Id, $"story-{i}", "Unable to reconcile returned attempts", false, failedAt.AddSeconds(i), default);
            await db.SaveChangesAsync();
        }
        var auth = new ScopedActionAuthorizationService(db);
        var recovery = new WorkRecoveryService(db, native, auth, new WorkIssueService(db, auth, TimeProvider.System));
        var before = await recovery.ReadAsync(setup.Organization.Id, manager, new(task.Id), default);
        Assert.DoesNotContain(package.Version, before.Explanation);
        package.ImportedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var changed = await recovery.ReadAsync(setup.Organization.Id, manager, new(task.Id), default);
        Assert.Contains(package.Version, changed.Explanation);
        Assert.Equal("Unknown", changed.Eligibility);
        Assert.NotEqual(before.Basis, changed.Basis);
        var selfResume = await native.RequeueAsync(setup.Organization.Id, new(setup.Agent.Id, installation.Id), new(task.Id, task.Revision, "owner-observed-draft"));
        Assert.Equal(Wire.PersonalTodoStatuses.Blocked, selfResume.Status);
        task.BlockReason = "Awaiting owner direction"; task.Revision++;
        await db.SaveChangesAsync();
        Assert.DoesNotContain(package.Version, (await recovery.ReadAsync(setup.Organization.Id, manager, new(task.Id), default)).Explanation);
    }

    [Theory]
    [InlineData("work.issues.read.v1", "Permitted")]
    [InlineData("work.issues.reconcile.v1", "AlternativeAvailable")]
    [InlineData("external.planning.read", "Permitted")]
    public async Task OwnerOperationFactsRecoverHistoricalDenialButRetainRevocationHumanHoldAndRepeatStop(string failedCapability, string expectedEligibility)
    {
        await using var db = CreateDb(); var setup = Seed(db); await db.SaveChangesAsync();
        var native = new PersonalTodoService(db, TimeProvider.System);
        var manager = new PersonalTodoActor(setup.FirstManager.Id, null);
        var projectBoard = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id,
            TeamId = Guid.NewGuid(), WorkstreamId = Guid.NewGuid(), ManagerOrganizationUserId = setup.Agent.Id };
        db.WorkBoards.Add(projectBoard);
        var installation = new AgentInstallation { Id = setup.Agent.AgentInstallationId!.Value, BusinessId = setup.Organization.Id.ToString(), IsEnabled = true };
        db.AgentInstallations.Add(installation);
        db.AgentInstallationGrants.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { Wire.WorkIssueCapabilities.Read, Wire.WorkIssueCapabilities.Reconcile, Wire.WorkIssueCapabilities.Propose, failedCapability }) });
        await db.SaveChangesAsync();
        var retained = await native.AddAsync(setup.Organization.Id, manager, Add("Retained scoped planning", "retained-facts", setup.Agent.Id) with {
            WorkContext = new(BoardId: projectBoard.Id) });
        var task = await db.CoreWorkTasks.SingleAsync(t => t.Id == retained.Id);
        var diagnostic = "agent-failure:v1;code=platform.capability.denied;capability=" + failedCapability + ";retryable=false";
        for (int i = 0; i < 2; i++) { await AgentTicketFeedback.RecordFailureAsync(db, task, installation.Id, "denial-" + i, diagnostic, false, DateTimeOffset.UtcNow.AddMilliseconds(i), default); await db.SaveChangesAsync(); }
        var authorization = new ScopedActionAuthorizationService(db);
        var facts = new WorkRecoveryService(db, native, authorization, new WorkIssueService(db, authorization, TimeProvider.System), [new ExternalReadPolicy(db, authorization)]);
        var denied = await facts.ReadAsync(setup.Organization.Id, manager, new(task.Id), default);
        Assert.Equal("Denied", denied.Eligibility);
        var grant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installation.Id,
            Action = WorkBoardActions.Read, ScopeKind = GrantScopeKind.Team, ScopeId = projectBoard.TeamId };
        db.ScopedActionGrants.Add(grant); await db.SaveChangesAsync();
        var allowed = await facts.ReadAsync(setup.Organization.Id, manager, new(task.Id), default);
        Assert.True(allowed.CanResume); Assert.Equal(expectedEligibility, allowed.Eligibility); Assert.NotEqual(denied.Basis, allowed.Basis);
        // An unrelated ticket change cannot produce another recovery basis.
        db.CoreWorkTasks.Add(new() { Id = Guid.NewGuid(), OrganizationId = setup.Organization.Id, BoardId = projectBoard.Id, Title = "Independent drafting" }); await db.SaveChangesAsync();
        Assert.Equal(allowed.Basis, (await facts.ReadAsync(setup.Organization.Id, manager, new(task.Id), default)).Basis);
        var ownerAttempt = await native.RequeueAsync(setup.Organization.Id, new(setup.Agent.Id, installation.Id), new(task.Id, task.Revision, "owner-no-op"));
        Assert.Equal(Wire.PersonalTodoStatuses.Blocked, ownerAttempt.Status);
        var resumed = await native.RequeueAsync(setup.Organization.Id, manager, new(task.Id, task.Revision, "reviewed-recovery"));
        Assert.Equal(Wire.PersonalTodoStatuses.Ready, resumed.Status);
        // Same operation fails again: its causal authority basis stays unchanged, so the SDK's receipt prevents an infinite retry loop.
        await AgentTicketFeedback.RecordFailureAsync(db, task, installation.Id, "new-failure", diagnostic, false, DateTimeOffset.UtcNow.AddSeconds(1), default); await db.SaveChangesAsync();
        Assert.Equal(allowed.Basis, (await facts.ReadAsync(setup.Organization.Id, manager, new(task.Id), default)).Basis);
        grant.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync();
        Assert.Equal("Denied", (await facts.ReadAsync(setup.Organization.Id, manager, new(task.Id), default)).Eligibility);
        task.BlockReason = "Awaiting a reserved owner decision"; task.Revision++; await db.SaveChangesAsync();
        Assert.Equal("Unknown", (await facts.ReadAsync(setup.Organization.Id, manager, new(task.Id), default)).Eligibility);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => facts.ReadAsync(setup.Organization.Id, new(setup.SecondManager.Id, null), new(task.Id), default));
    }
    private sealed class ExternalReadPolicy(CSweet.Infrastructure.Persistence.CSweetDbContext db, CSweet.Application.Security.IScopedActionAuthorizationService authorization) : IWorkRecoveryAuthorizationPolicy
    {
        public bool Handles(string capability) => capability == "external.planning.read";
        public async Task<WorkRecoveryAuthorization> EvaluateAsync(WorkRecoveryOperation operation, CancellationToken ct)
        {
            var grant = await WorkResourceAuthorization.AuthorizeBoardAsync(db, authorization, operation.OrganizationId,
                GrantSubjectKind.AgentInstallation, operation.OwnerInstallationId!.Value, WorkBoardActions.Read, operation.BoardId, ct);
            return new(grant.Allowed, CSweet.Infrastructure.Persistence.MutationContent.Digest(new { grant.GrantId, grant.GrantRevision, grant.Allowed }),
                "The external protocol uses the same verified current board-read authority.");
        }
    }

}
