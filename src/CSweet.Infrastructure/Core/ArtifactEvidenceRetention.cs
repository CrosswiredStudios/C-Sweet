using CSweet.Domain.Core;
using CSweet.Contracts.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Publish immutable observations without creating a review or accepting a verdict.</summary>
public static class ArtifactEvidenceRetention
{
    // Upgrade only review routing. Keep old revision states/pointers readable by running
    // pre-upgrade QA workers, and preserve all content, digests and historical decisions.
    public static async Task<int> DismissLegacyApprovalsAsync(CSweetDbContext db, DateTimeOffset now,
        CancellationToken token = default)
    {
        var artifacts = await db.CoreArtifacts.Where(x =>
            x.DocumentType == ArtifactDocumentTypes.RecoveredQaEvidence &&
            x.ApprovalStatus == ApprovalStatus.Pending).ToListAsync(token);
        foreach (var artifact in artifacts)
        {
            artifact.ApprovalStatus = ApprovalStatus.NotRequired;
            if (artifact.SubmittedRevisionId is { } revisionId)
                await ArtifactReviewJobLifecycle.CompleteRevisionAsync(db, artifact.OrganizationId, revisionId, now, token);
        }
        if (artifacts.Count != 0) await db.SaveChangesAsync(token);
        return artifacts.Count;
    }

    public static async Task RetainAsync(CSweetDbContext db, Artifact artifact, ArtifactRevision revision,
        DateTimeOffset now, CancellationToken token)
    {
        if (!ArtifactDocumentTypes.IsRetainedEvidence(artifact.DocumentType))
            throw new InvalidOperationException("This document requires the normal review workflow.");
        if (artifact.ArchivedAt.HasValue)
            throw new InvalidOperationException("Archived evidence cannot be saved.");
        if (artifact.LatestRevisionId != revision.Id ||
            revision.Status is not (ArtifactRevisionStatus.Draft or ArtifactRevisionStatus.Submitted or ArtifactRevisionStatus.Retained))
            throw new InvalidOperationException("Only the latest draft or retained evidence can be saved.");

        revision.Status = ArtifactRevisionStatus.Retained;
        revision.SubmittedAt ??= now;
        artifact.SubmittedRevisionId = null;
        artifact.DocumentStatus = ArtifactDocumentStatus.Retained;
        artifact.ApprovalStatus = ApprovalStatus.NotRequired;
        artifact.UpdatedAt = now;
        await ArtifactReviewJobLifecycle.CompleteRevisionAsync(db, artifact.OrganizationId, revision.Id, now, token);
    }
}
