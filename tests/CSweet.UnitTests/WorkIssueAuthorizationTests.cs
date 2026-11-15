using CSweet.Contracts.Core;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Security;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkIssueAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentHumanOrInstalledAgentAuthorityCanReadAndReconcile(bool human)
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        if (human) { f.Manager.AgentInstallationId = null; await f.Db.SaveChangesAsync(); }
        await GrantAsync(f, WorkBoardActions.Read, GrantScopeKind.Board, f.Board.Id);
        await GrantAsync(f, WorkItemActions.RevisePlanning, GrantScopeKind.Board, f.Board.Id);
        await GrantAsync(f, ArtifactActions.Read, GrantScopeKind.Artifact, f.Source.DocumentId);
        var service = new WorkIssueService(f.Db, new ScopedActionAuthorizationService(f.Db), TimeProvider.System);
        Assert.Empty((await service.ReadAsync(f.Org, f.Manager.Id, new(f.Board.Id), default)).Issues);
        Assert.Single((await service.ReconcileAsync(f.Org, f.Manager.Id, Request(f), default)).Issues);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("other-board")]
    [InlineData("other-organization")]
    [InlineData("old-installation")]
    [InlineData("not-accountable")]
    [InlineData("inactive")]
    public async Task PermissionResolutionDoesNotBroadenAuthority(string condition)
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var grant = await GrantAsync(f, WorkItemActions.RevisePlanning, GrantScopeKind.Board, f.Board.Id);
        await GrantAsync(f, ArtifactActions.Read, GrantScopeKind.Artifact, f.Source.DocumentId);
        switch (condition)
        {
            case "revoked": grant.RevokedAt = DateTimeOffset.UtcNow; break;
            case "expired": grant.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "other-board": grant.ScopeId = Guid.NewGuid(); break;
            case "other-organization": grant.OrganizationId = Guid.NewGuid(); break;
            case "old-installation": f.Manager.AgentInstallationId = Guid.NewGuid(); break;
            case "not-accountable": f.Board.ManagerOrganizationUserId = f.Specialist.Id; break;
            case "inactive": f.Manager.IsActive = false; break;
        }
        await f.Db.SaveChangesAsync();
        var service = new WorkIssueService(f.Db, new ScopedActionAuthorizationService(f.Db), TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReconcileAsync(f.Org, f.Manager.Id, Request(f), default));
        Assert.Empty(await f.Db.WorkIssueCases.ToListAsync());
    }

    [Fact]
    public async Task APreviouslyDeniedReconciliationRecoversAndReplaysWithoutDuplicatingIssues()
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        var service = new WorkIssueService(f.Db, new ScopedActionAuthorizationService(f.Db), TimeProvider.System);
        var request = Request(f);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReconcileAsync(f.Org, f.Manager.Id, request, default));
        await GrantAsync(f, WorkItemActions.RevisePlanning, GrantScopeKind.Board, f.Board.Id);
        await GrantAsync(f, ArtifactActions.Read, GrantScopeKind.Artifact, f.Source.DocumentId);
        var first = await service.ReconcileAsync(f.Org, f.Manager.Id, request, default);
        var replay = await service.ReconcileAsync(f.Org, f.Manager.Id, request, default);
        Assert.Equal(Assert.Single(first.Issues).Id, Assert.Single(replay.Issues).Id);
        Assert.Single(await f.Db.WorkIssueCases.ToListAsync());
    }

    [Fact]
    public async Task AuthorityChangesWakeTheCurrentManagerAtomicallyWithoutGrantingACommand()
    {
        await using var f = new WorkIssueLifecycleTests.Fixture(); await f.SeedAsync();
        f.Specialist.ReportsToOrganizationUserId = f.Manager.Id; await f.Db.SaveChangesAsync();
        var grant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = f.Specialist.AgentInstallationId!.Value, Action = WorkBoardActions.Read, ScopeKind = GrantScopeKind.Team, ScopeId = Guid.NewGuid() };
        f.Db.ScopedActionGrants.Add(grant); await f.Db.SaveChangesAsync();
        var wake = Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(e => e.EventType == Wire.WorkRecoveryEvents.Changed).ToArrayAsync());
        Assert.Equal(f.Manager.AgentInstallationId, wake.TargetInstallationId);
        await f.Db.SaveChangesAsync();
        Assert.Single(await f.Db.AgentPlatformEventOutbox.Where(e => e.EventType == Wire.WorkRecoveryEvents.Changed).ToArrayAsync());
        grant.RevokedAt = DateTimeOffset.UtcNow; await f.Db.SaveChangesAsync();
        Assert.Equal(2, await f.Db.AgentPlatformEventOutbox.CountAsync(e => e.EventType == Wire.WorkRecoveryEvents.Changed));
        Assert.Empty(await f.Db.CoreWorkTasks.ToArrayAsync()); // wake hints are not execution grants
    }

    private static Wire.ReconcileWorkIssuesRequest Request(WorkIssueLifecycleTests.Fixture f) =>
        new(f.Board.Id, f.Source, [new("future-content", "Own the future content outcome", "Retained evidence", ["content"])], "recover-existing");

    private static async Task<ScopedActionGrant> GrantAsync(WorkIssueLifecycleTests.Fixture f, string action, GrantScopeKind scope, Guid id)
    {
        var grant = new ScopedActionGrant { Id = Guid.NewGuid(), OrganizationId = f.Org,
            SubjectKind = f.Manager.AgentInstallationId.HasValue ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser,
            SubjectId = f.Manager.AgentInstallationId ?? f.Manager.Id, Action = action, ScopeKind = scope, ScopeId = id,
            GrantedAt = DateTimeOffset.UtcNow };
        f.Db.ScopedActionGrants.Add(grant); await f.Db.SaveChangesAsync(); return grant;
    }
}
