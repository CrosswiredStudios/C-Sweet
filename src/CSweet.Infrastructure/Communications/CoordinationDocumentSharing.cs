using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Security;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Communications;

/// <summary>Explicit document references share read access with the other authenticated session participant.</summary>
public static class CoordinationDocumentSharing
{
    public static async Task ShareAsync(CSweetDbContext db, Guid organizationId, Guid authorId,
        Guid authorInstallationId, Guid recipientInstallationId, AgentCoordinationArtifactSubmission? submission,
        CancellationToken token)
    {
        if (submission?.Payload.ValueKind != JsonValueKind.Object ||
            !submission.Payload.TryGetProperty("documentReferences", out var references)) return;
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() > 8)
            throw new ArgumentException("Coordination document references must contain at most eight exact revisions.");
        var now = DateTimeOffset.UtcNow;
        var validated = new HashSet<Guid>();
        foreach (var reference in references.EnumerateArray())
        {
            var documentId = reference.GetProperty("documentId").GetGuid();
            var revisionId = reference.GetProperty("revisionId").GetGuid();
            var digest = reference.GetProperty("contentSha256").GetString();
            var document = await db.CoreArtifacts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == documentId && x.OrganizationId == organizationId && x.ArchivedAt == null, token);
            if (document is null || (document.CreatedByOrganizationUserId != authorId && document.StewardOrganizationUserId != authorId) ||
                !await db.ScopedActionGrants.AnyAsync(x => x.OrganizationId == organizationId &&
                    x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == authorInstallationId &&
                    x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId == documentId && x.Action == ArtifactActions.Read &&
                    x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now), token))
                throw new UnauthorizedAccessException("Only an authorized document creator or steward can share it in coordination.");
            if (!await db.ArtifactRevisions.AnyAsync(x => x.OrganizationId == organizationId && x.ArtifactId == documentId &&
                x.Id == revisionId && x.ContentSha256 == digest, token))
                throw new ArgumentException("A shared document reference must identify its exact revision and hash.");
            validated.Add(documentId);
        }
        foreach (var documentId in validated)
        {
            if (await db.ScopedActionGrants.AnyAsync(x => x.OrganizationId == organizationId &&
                x.SubjectKind == GrantSubjectKind.AgentInstallation && x.SubjectId == recipientInstallationId &&
                x.ScopeKind == GrantScopeKind.Artifact && x.ScopeId == documentId && x.Action == ArtifactActions.Read &&
                x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now), token)) continue;
            db.ScopedActionGrants.Add(new() { Id = Guid.NewGuid(), OrganizationId = organizationId,
                SubjectKind = GrantSubjectKind.AgentInstallation, SubjectId = recipientInstallationId,
                ScopeKind = GrantScopeKind.Artifact, ScopeId = documentId, Action = ArtifactActions.Read,
                GrantedBySubjectKind = GrantSubjectKind.AgentInstallation, GrantedBySubjectId = authorInstallationId, GrantedAt = now });
        }
    }
}
