using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CollaborationReviewAuthorityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubmittedBriefGrantsReviewOnlyToItsCreatorsAssignedManager(bool isManager)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var author = Guid.NewGuid(); var reviewer = Guid.NewGuid();
        var installation = Guid.NewGuid(); var reviewerInstallation = Guid.NewGuid();
        var document = Guid.NewGuid(); var revision = Guid.NewGuid();
        db.CoreOrganizationUsers.AddRange(
            new() { Id = author, OrganizationId = org, AgentInstallationId = installation, EmployeeType = EmployeeType.Agent,
                ReportsToOrganizationUserId = isManager ? reviewer : Guid.NewGuid() },
            new() { Id = reviewer, OrganizationId = org, AgentInstallationId = reviewerInstallation, EmployeeType = EmployeeType.Agent });
        db.CoreArtifacts.Add(new() { Id = document, OrganizationId = org, CreatedByOrganizationUserId = author,
            StewardOrganizationUserId = reviewer, LatestRevisionId = revision, DocumentType = "production-brief",
            Revisions = [new() { Id = revision, OrganizationId = org, ArtifactId = document, Status = ArtifactRevisionStatus.Draft }] });
        db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, SubjectKind = GrantSubjectKind.AgentInstallation,
            SubjectId = installation, ScopeKind = GrantScopeKind.Artifact, ScopeId = document, Action = ArtifactActions.Submit });
        await db.SaveChangesAsync();
        var handler = new ArtifactCapabilityHandler(db, null!, new TestAuditEventWriter(), TimeProvider.System);
        var session = new AgentSession("session", "producer", installation.ToString(), org.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { PlatformCapabilities.ArtifactSubmit }, 1));
        var request = new RequestCapability { RequestId = "submit", Capability = PlatformCapabilities.ArtifactSubmit,
            Payload = JsonPayload.From(new SubmitArtifactRevision(document, revision, "submit", ReviewerOrganizationUserId: reviewer), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var results = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, default)) results.Add(result);
        Assert.True(Assert.Single(results).Succeeded);
        var actions = db.ScopedActionGrants.Where(x => x.SubjectId == reviewerInstallation).Select(x => x.Action).ToList();
        Assert.Equal(isManager, actions.Contains(ArtifactActions.Decide));
        Assert.Equal(isManager, actions.Contains(ArtifactActions.Read));
        Assert.DoesNotContain(ArtifactActions.Revise, actions);
    }
}
