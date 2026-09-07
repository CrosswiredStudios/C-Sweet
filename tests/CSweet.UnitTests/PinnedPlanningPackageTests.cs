using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class PinnedPlanningPackageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentPackageKeepsItsApprovedRevisionAndRejectsDraftPins(bool accepted)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var author = Guid.NewGuid(); var installation = Guid.NewGuid();
        var document = Guid.NewGuid(); var first = Guid.NewGuid(); var later = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new() { Id = author, OrganizationId = org, AgentInstallationId = installation, EmployeeType = EmployeeType.Agent });
        db.CoreArtifacts.Add(new() { Id = document, OrganizationId = org, CreatedByOrganizationUserId = author,
            DocumentType = "production-brief", LatestRevisionId = later, AcceptedRevisionId = later,
            Revisions = [new() { Id = first, OrganizationId = org, ArtifactId = document, Status = accepted ? ArtifactRevisionStatus.Accepted : ArtifactRevisionStatus.Draft },
                new() { Id = later, OrganizationId = org, ArtifactId = document, Status = ArtifactRevisionStatus.Accepted }] });
        foreach (var action in new[] { ArtifactActions.Read, ArtifactActions.Decide })
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = org, SubjectKind = GrantSubjectKind.AgentInstallation,
                SubjectId = installation, ScopeKind = GrantScopeKind.Artifact, ScopeId = document, Action = action });
        await db.SaveChangesAsync();
        var handler = new ArtifactCapabilityHandler(db, null!, new TestAuditEventWriter(), TimeProvider.System);
        var session = new AgentSession("session", "producer", installation.ToString(), org.ToString(), "runtime", "tick",
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { PlatformCapabilities.ArtifactPackageCreate, PlatformCapabilities.ArtifactPackageDecide }, 1));
        async Task<CapabilityResult> Invoke(string capability, object input)
        {
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(session, new() { RequestId = "request", Capability = capability,
                Payload = JsonPayload.From(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, default)) results.Add(result);
            return Assert.Single(results);
        }
        var created = await Invoke(PlatformCapabilities.ArtifactPackageCreate, new CreateArtifactPackage("Planning", "planning",
            [new CSweet.Agent.SDK.ArtifactPackageMember(document, 0, "production-brief", first)], "create"));
        Assert.Equal(accepted, created.Succeeded);
        if (!accepted) { Assert.Empty(db.ArtifactPackages); return; }
        var packageId = created.Payload.ToElement().GetProperty("id").GetGuid();
        var decided = await Invoke(PlatformCapabilities.ArtifactPackageDecide, new { packageId, idempotencyKey = "decide" });
        Assert.True(decided.Succeeded, decided.Error);
        Assert.Equal(first, decided.Payload.ToElement().GetProperty("members")[0].GetProperty("acceptedRevisionId").GetGuid());
    }
}
