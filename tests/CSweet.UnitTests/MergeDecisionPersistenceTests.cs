using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Security;
using CSweet.Application.SourceControl;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.Setup;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class MergeDecisionPersistenceTests
{
    [Theory]
    [InlineData("Approve", false, true, SourceControlPublicationStatus.ReadyToMerge)]
    [InlineData("Approve", true, true, SourceControlPublicationStatus.AwaitingAdministratorApproval)]
    [InlineData("Reject", false, false, SourceControlPublicationStatus.Superseded)]
    [InlineData("Approve", false, false, SourceControlPublicationStatus.AwaitingValidation)]
    public async Task BrokerDecisionPersistsPublicationAndExactSignedAuthorization(string decision, bool administrator, bool qa,
        SourceControlPublicationStatus expected)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var installation = Guid.NewGuid(); var employee = Guid.NewGuid(); var repository = Guid.NewGuid();
        var team = new OrganizationTeam { Id = Guid.NewGuid(), OrganizationId = org, LeadOrganizationUserId = employee, Name = "Game" };
        var board = new WorkBoard { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, Name = "Game" };
        var item = new WorkTask { Id = Guid.NewGuid(), OrganizationId = org, BoardId = board.Id, AssignmentRevision = 5 };
        var workspace = new SourceControlWorkspace { Id = Guid.NewGuid(), OrganizationId = org, WorkItemId = item.Id, AssignmentRevision = 5 };
        var publication = new SourceControlPublication { Id = Guid.NewGuid(), OrganizationId = org, WorkspaceId = workspace.Id,
            ReviewPatch = "Review base: abc\ndiff --git a/game.js b/game.js\n+gameplay",
            ChangedFilesJson = "[\"game.js\"]",
            RepositoryId = repository, CommitSha = new string('a', 40), Status = SourceControlPublicationStatus.AwaitingValidation };
        var policy = new TeamRepositoryPolicy { Id = Guid.NewGuid(), OrganizationId = org, TeamId = team.Id, RepositoryId = repository,
            Revision = 3, MergeApprovalMode = administrator ? TeamMergeApprovalMode.LeadAndAdministratorApproval : TeamMergeApprovalMode.LeadAuthorizedAutoMerge };
        db.AddRange(team, board, item, workspace, publication, policy,
            new SourceControlRepository { Id = repository, OrganizationId = org, Name = "Game" },
            new OrganizationUser { Id = employee, OrganizationId = org, AgentInstallationId = installation, IsActive = true, DisplayName = "Lead" });
        if (qa) db.SourceControlValidations.Add(new SourceControlValidation { Id = Guid.NewGuid(), OrganizationId = org,
            PublicationId = publication.Id, CommitSha = publication.CommitSha, ResultsJson = "[{\"command\":\"npm test\",\"succeeded\":true,\"exitCode\":0}]", Status = SourceControlValidationStatus.Passed });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var signer = new Signer();
        var handler = new GitWorkspaceCapabilityHandler(db, new UnavailableTrustedGitHostClient(), new Authorization(), signer);
        var session = new AgentSession("session", "lead", installation.ToString(), org.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { GitMergeCapabilities.Authorize, GitMergeCapabilities.Review }, 1));
        var reviewRequest = new RequestCapability { RequestId = "review", Capability = GitMergeCapabilities.Review,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new ReviewGitMergeRequest(item.Id, 5, "review-1"))) };
        var reviews = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, reviewRequest, default)) reviews.Add(result);
        var reviewResult = Assert.Single(reviews);
        Assert.True(reviewResult.Succeeded, reviewResult.Error);
        var review = reviewResult.Payload.ToElement().Deserialize<GitMergeReview>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(publication.ReviewPatch, review.DiffSummary);
        Assert.Equal(qa ? 1 : 0, review.QualityEvidence.Count);
        Assert.Contains("Passing QA for the exact candidate SHA", review.RequiredChecks);
        Assert.DoesNotContain("game.js", review.RequiredChecks);
        db.ChangeTracker.Clear();
        var request = new RequestCapability { RequestId = "decision", Capability = GitMergeCapabilities.Authorize,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new AuthorizeGitMergeRequest(item.Id, 5, publication.Id,
                publication.CommitSha, decision, "Review findings", "decision-1"))) };
        var responses = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) responses.Add(result);
        Assert.Equal(qa || decision == "Reject", Assert.Single(responses).Succeeded);
        db.ChangeTracker.Clear();
        var saved = await db.SourceControlPublications.SingleAsync();
        Assert.Equal(expected, saved.Status);
        Assert.Equal(qa || decision == "Reject" ? 2 : 1, saved.Revision);
        if (qa && decision == "Approve")
        {
            var authorization = await db.SourceControlMergeAuthorizations.SingleAsync();
            Assert.Equal(employee, authorization.AuthorizedByOrganizationUserId);
            Assert.Equal(publication.CommitSha, authorization.CommitSha);
            Assert.Equal(3, authorization.TeamPolicyRevision);
            Assert.Equal("signed-exact-decision", authorization.DecisionSignature);
            Assert.Equal(publication.Id, signer.Decision!.PublicationId);
            var savedId = authorization.Id;
            var savedTime = authorization.AuthorizedAt;
            await foreach (var retry in handler.HandleAsync(session, request, default)) Assert.True(retry.Succeeded, retry.Error);
            db.ChangeTracker.Clear();
            var replayed = await db.SourceControlMergeAuthorizations.SingleAsync();
            Assert.Equal(savedId, replayed.Id);
            Assert.Equal(savedTime, replayed.AuthorizedAt);
            Assert.Equal(2, (await db.SourceControlPublications.SingleAsync()).Revision);
            replayed.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await foreach (var revoked in handler.HandleAsync(session, request, default)) Assert.False(revoked.Succeeded);
            Assert.Single(db.SourceControlMergeAuthorizations);
        }
        else Assert.Empty(db.SourceControlMergeAuthorizations);
    }

    private sealed class Authorization : IScopedActionAuthorizationService
    {
        public Task<ScopedAuthorizationDecision> AuthorizeAsync(Guid organizationId, GrantSubjectKind subjectKind, Guid subjectId,
            string action, GrantScopeKind resourceScopeKind, Guid? resourceScopeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScopedAuthorizationDecision(true, action));
    }
    private sealed class Signer : ISourceControlDecisionSigner
    {
        public SourceControlMergeDecision? Decision { get; private set; }
        public string Sign(SourceControlMergeDecision decision) { Decision = decision; return "signed-exact-decision"; }
        public bool Verify(SourceControlMergeDecision decision, string signature) => signature == "signed-exact-decision";
    }
}
