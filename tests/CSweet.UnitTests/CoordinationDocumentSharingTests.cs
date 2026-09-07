using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class CoordinationDocumentSharingTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task SharingRequiresOwnershipReadGrantAndExactRevision(bool owns, bool granted, bool exact)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var installation = Guid.NewGuid();
        var recipient = Guid.NewGuid(); var document = Guid.NewGuid(); var revision = Guid.NewGuid();
        db.CoreArtifacts.Add(new() { Id = document, OrganizationId = org, CreatedByOrganizationUserId = owns ? actor : Guid.NewGuid() });
        db.ArtifactRevisions.Add(new() { Id = revision, OrganizationId = org, ArtifactId = document, ContentSha256 = "exact-hash" });
        if (granted) db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = org,
            SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = installation, ScopeKind = GrantScopeKind.Artifact,
            ScopeId = document, Action = ArtifactActions.Read });
        await db.SaveChangesAsync();
        var artifact = new AgentCoordinationArtifactSubmission("test.v1", "1.0", "test", 1, true,
            JsonSerializer.SerializeToElement(new { documentReferences = new[] { new { documentId = document,
                revisionId = revision, contentSha256 = exact ? "exact-hash" : "wrong" } } }));
        if (owns && granted && exact)
        {
            await CoordinationDocumentSharing.ShareAsync(db, org, actor, installation, recipient, artifact, default);
            await db.SaveChangesAsync();
            await CoordinationDocumentSharing.ShareAsync(db, org, actor, installation, recipient, artifact, default);
            await db.SaveChangesAsync();
            var access = Assert.Single(db.ScopedActionGrants.Where(x => x.SubjectId == recipient));
            Assert.Equal(ArtifactActions.Read, access.Action); Assert.Equal(document, access.ScopeId);
            Assert.Equal(installation, access.GrantedBySubjectId);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => CoordinationDocumentSharing.ShareAsync(db, org, actor, installation, recipient, artifact, default));
            Assert.Empty(db.ScopedActionGrants.Where(x => x.SubjectId == recipient));
        }
    }
}
