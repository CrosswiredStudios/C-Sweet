using System.Text.Json;
using CSweet.Application.Security;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WorkIssueLifecycleTests
{
    [Fact]
    public async Task RewordingKeepsIdentityOneCommissionAndDoesNotWakeASecondRound()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var first = await f.Save("Ensure meaningful audio ownership", "first");
        var issue = Assert.Single(first.Issues);
        var commission = new Wire.CommissionWorkIssuesRequest(f.Board.Id, f.Specialist.Id, [issue.Id], f.Source, "Reconcile responsibility", "consult");
        var intent = await f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId!.Value, commission, default);
        f.Db.ChangeTracker.Clear(); // Simulate restart after the platform accepted the intent but its reply was lost.
        var replay = await f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId.Value, commission, default);
        Assert.Equal(intent.Id, replay.Id); Assert.Single(f.Db.WorkIssueCommissions);
        var wakes = f.Db.AgentPlatformEventOutbox.Count();
        var revised = Assert.Single((await f.Save("Audio responsibility must have a meaningful owner", "reword", issue)).Issues);
        Assert.Equal(issue.Id, revised.Id); Assert.Equal(wakes, f.Db.AgentPlatformEventOutbox.Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId.Value,
            commission with { IdempotencyKey = "duplicate" }, default));
        Assert.Single(f.Db.WorkIssueCommissions);
    }

    [Fact]
    public async Task AdviceIsNotResolutionAndExactCurrentDecisionsCannotBeHiddenByProgress()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var issue = Assert.Single((await f.Save("Technical feasibility", "open")).Issues);
        var decision = new WorkstreamDecisionRecord { Id = Guid.NewGuid(), OrganizationId = f.Org, WorkstreamId = f.Board.WorkstreamId!.Value, Status = "Pending" };
        f.Db.Add(decision); await f.Db.SaveChangesAsync();
        issue = Assert.Single((await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
            [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) { IssueId = issue.Id, ExpectedRevision = issue.Revision,
                Phase = "AwaitingDecision", Links = [new("Decision", decision.Id)] }], "linked"), default)).Issues);
        var close = new Wire.ReconcileWorkIssuesRequest(f.Board.Id, f.Source,
            [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) { IssueId = issue.Id, ExpectedRevision = issue.Revision,
                Status = "Resolved", ResolutionEvidence = "The current reviewed plan owns the obligation" }], "resolve");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ReconcileAsync(f.Org, f.Manager.Id, close, default));
        Assert.Equal("Open", f.Db.WorkIssueCases.Single().Status);
        decision.Status = "Decided"; await f.Db.SaveChangesAsync();
        var resolved = Assert.Single((await f.Service.ReconcileAsync(f.Org, f.Manager.Id, close, default)).Issues);
        Assert.Equal("Resolved", resolved.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Save("Reworded feasibility concern", "reopen", resolved));
        Assert.Empty((await f.Service.ReadAsync(f.Org, f.Manager.Id, new(f.Board.Id), default)).Issues);
        Assert.Single((await f.Service.ReadAsync(f.Org, f.Manager.Id, new(f.Board.Id, true), default)).Issues);
    }

    [Fact]
    public async Task ChangedAcceptedBasisCreatesAnAuditedEpisodeAndMismatchedMutationIsRejected()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var issue = Assert.Single((await f.Save("Review license ownership", "open")).Issues);
        var closed = Assert.Single((await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
            [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) { IssueId = issue.Id, ExpectedRevision = issue.Revision,
                Status = "Resolved", ResolutionEvidence = "Reviewed current rights responsibility" }], "closed"), default)).Issues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Save("Different content", "open"));
        await f.ChangeSourceAsync();
        var next = Assert.Single((await f.Save("The new licensed assets need review", "new-basis", closed)).Issues);
        Assert.NotEqual(closed.Id, next.Id); Assert.Equal(closed.Id, next.PreviousIssueId); Assert.Equal(2, next.Episode);
    }

    [Fact]
    public async Task NewRecordedEvidenceCanReopenTheSameObligationWithoutChangingApprovedScope()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var issue = Assert.Single((await f.Save("Review collision feasibility", "open")).Issues);
        var closed = Assert.Single((await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
            [new(issue.ObligationKey, issue.Statement, issue.Detail, issue.ScopeKeys) { IssueId = issue.Id, ExpectedRevision = issue.Revision,
                Status = "Resolved", ResolutionEvidence = "The accepted plan owns the collision responsibility" }], "closed"), default)).Issues);
        var finding = new WorkTask { Id = Guid.NewGuid(), OrganizationId = f.Org, BoardId = f.Board.Id, Title = "New high-speed collision finding" };
        f.Db.Add(finding); await f.Db.SaveChangesAsync();
        var reopened = Assert.Single((await f.Service.ReconcileAsync(f.Org, f.Manager.Id, new(f.Board.Id, f.Source,
            [new(issue.ObligationKey, issue.Statement, "New reviewed finding changes the basis", issue.ScopeKeys) {
                IssueId = closed.Id, ExpectedRevision = closed.Revision, ReopeningEvidence = new("WorkItem", finding.Id) }], "new-observation"), default)).Issues);
        Assert.Equal(2, reopened.Episode); Assert.Equal(closed.Id, reopened.PreviousIssueId); Assert.Equal("Open", reopened.Status);
        Assert.Equal(2, (await f.Service.ReadAsync(f.Org, f.Manager.Id, new(f.Board.Id, true), default)).Issues.Count);
    }

    [Fact]
    public async Task StaleBasisForeignLinksAndRemovedManagementCannotCommissionOrResolve()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var issue = Assert.Single((await f.Save("Responsibility", "open")).Issues);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReconcileAsync(f.Org, f.Manager.Id,
            new(f.Board.Id, f.Source, [new("bad-link", "Responsibility", "Full detail", []) { Links = [new("Decision", Guid.NewGuid())] }], "foreign"), default));
        f.Db.ChangeTracker.Clear();
        await f.ChangeSourceAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId!.Value,
            new(f.Board.Id, f.Specialist.Id, [issue.Id], issue.Source, "Reconcile", "stale"), default));
        var board = await f.Db.WorkBoards.SingleAsync(); board.ManagerOrganizationUserId = f.Specialist.Id; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Save("Not the current manager", "removed"));
    }

    [Fact]
    public async Task ChangedAuthorityRoutesFailureOnceToTheCurrentManagerInsteadOfRetryingForever()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var issue = Assert.Single((await f.Save("A scoped responsibility", "open")).Issues);
        await f.Service.CommissionAsync(f.Org, f.Manager.Id, f.Manager.AgentInstallationId!.Value,
            new(f.Board.Id, f.Specialist.Id, [issue.Id], f.Source, "Reconcile the source", "intent"), default);
        f.Board.ManagerOrganizationUserId = f.Specialist.Id; await f.Db.SaveChangesAsync();
        var dispatcher = new WorkIssueCommissionDispatcher(f.Db, f.Service, null!, null!, TimeProvider.System);
        await dispatcher.DispatchAsync(default);
        var current = await f.Db.WorkIssueCommissions.SingleAsync(); Assert.Equal("NeedsAuthority", current.Status);
        var responsibility = await f.Db.WorkIssueCases.SingleAsync(); Assert.Equal(f.Specialist.Id, responsibility.AccountableEmployeeId);
        Assert.Equal("Open", responsibility.Status); Assert.Equal("AwaitingReview", responsibility.Phase);
        var wakes = f.Db.AgentPlatformEventOutbox.Count(); await dispatcher.DispatchAsync(default);
        Assert.Equal(wakes, f.Db.AgentPlatformEventOutbox.Count());
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public Guid Org { get; } = Guid.NewGuid();
        public CSweetDbContext Db { get; }
        public WorkIssueService Service { get; }
        public OrganizationUser Manager { get; } = new() { Id = Guid.NewGuid(), AgentInstallationId = Guid.NewGuid(), IsActive = true };
        public OrganizationUser Specialist { get; } = new() { Id = Guid.NewGuid(), AgentInstallationId = Guid.NewGuid(), IsActive = true };
        public WorkBoard Board { get; } = new() { Id = Guid.NewGuid(), WorkstreamId = Guid.NewGuid() };
        public Wire.WorkApprovedSource Source { get; private set; } = new(Guid.NewGuid(), Guid.NewGuid(), "approved-digest");
        public Fixture(DbContextOptions<CSweetDbContext>? options = null)
        {
            Db = new(options ?? new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Service = new(Db, new Grants(), TimeProvider.System);
        }
        public async Task SeedAsync()
        {
            Manager.OrganizationId = Specialist.OrganizationId = Board.OrganizationId = Org; Board.ManagerOrganizationUserId = Manager.Id;
            Db.AddRange(Manager, Specialist, Board, new Artifact { Id = Source.DocumentId, OrganizationId = Org, WorkstreamId = Board.WorkstreamId, AcceptedRevisionId = Source.RevisionId },
                new ArtifactRevision { Id = Source.RevisionId, OrganizationId = Org, ArtifactId = Source.DocumentId, Status = ArtifactRevisionStatus.Accepted, ContentSha256 = Source.Digest });
            await Db.SaveChangesAsync();
        }
        public async Task ChangeSourceAsync()
        {
            Source = Source with { RevisionId = Guid.NewGuid(), Digest = "changed-digest" };
            (await Db.CoreArtifacts.SingleAsync()).AcceptedRevisionId = Source.RevisionId;
            Db.Add(new ArtifactRevision { Id = Source.RevisionId, OrganizationId = Org, ArtifactId = Source.DocumentId, Status = ArtifactRevisionStatus.Accepted, ContentSha256 = Source.Digest });
            await Db.SaveChangesAsync();
        }
        public Task<Wire.WorkIssueDirectory> Save(string statement, string key, Wire.WorkIssueCaseResponse? prior = null) => Service.ReconcileAsync(Org, Manager.Id,
            new(Board.Id, Source, [new("audio-owner", statement, "Complete retained source detail", ["audio"]) { IssueId = prior?.Id, ExpectedRevision = prior?.Revision }], key), default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Grants : IScopedActionAuthorizationService
    {
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid org, GrantSubjectKind kind, Guid subject, string action, GrantScopeKind scope, Guid? id, CancellationToken ct = default) => Task.FromResult(new ScopedAuthorizationDecision(true, action));
    }
}
