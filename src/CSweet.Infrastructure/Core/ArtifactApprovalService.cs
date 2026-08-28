using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ArtifactApprovalService : IArtifactApprovalService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditEventWriter;

    public ArtifactApprovalService(CSweetDbContext dbContext, IAuditEventWriter auditEventWriter)
    {
        _dbContext = dbContext;
        _auditEventWriter = auditEventWriter;
    }

    public async Task<IReadOnlyList<ApprovalResponse>> ListByArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CoreApprovals
            .Where(x => x.ArtifactId == artifactId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ToResponse())
            .ToListAsync(cancellationToken);
    }

    public async Task<CoreActionResponse> ApproveAsync(Guid artifactId, string? comment = null, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.CoreArtifacts
            .Include(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);

        if (artifact is null)
        {
            return Failure("not_found", "Artifact was not found.");
        }

        // Check if artifact is already approved - cannot re-approve without revision
        if (artifact.ApprovalStatus == ApprovalStatus.Approved)
        {
            return Failure("approval_conflict", "Artifact is already approved.");
        }

        var now = DateTimeOffset.UtcNow;
        var revision = CurrentOrMigratedRevision(artifact, now);
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ArtifactRevisionId = revision.Id,
            Status = ApprovalStatus.Approved,
            Comment = comment,
            DecidedAt = now,
            CreatedAt = now
        };

        artifact.ApprovalStatus = ApprovalStatus.Approved;
        artifact.AcceptedRevisionId = revision.Id;
        artifact.SubmittedRevisionId = null;
        artifact.DocumentStatus = ArtifactDocumentStatus.Approved;
        revision.Status = ArtifactRevisionStatus.Accepted;
        revision.DecidedAt = now;
        artifact.UpdatedAt = now;

        _dbContext.CoreApprovals.Add(approval);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "artifact.approved",
            "Approval",
            approval.Id,
            $"Artifact '{artifact.Title}' approved.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Artifact approved successfully.", Approval: approval.ToResponse());
    }

    public async Task<CoreActionResponse> RejectAsync(Guid artifactId, string? comment = null, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.CoreArtifacts
            .Include(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);

        if (artifact is null)
        {
            return Failure("not_found", "Artifact was not found.");
        }

        var now = DateTimeOffset.UtcNow;
        var revision = CurrentOrMigratedRevision(artifact, now);
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ArtifactRevisionId = revision.Id,
            Status = ApprovalStatus.Rejected,
            Comment = comment,
            DecidedAt = now,
            CreatedAt = now
        };

        artifact.ApprovalStatus = ApprovalStatus.Rejected;
        artifact.SubmittedRevisionId = null;
        artifact.DocumentStatus = ArtifactDocumentStatus.ChangesRequested;
        revision.Status = ArtifactRevisionStatus.Rejected;
        revision.DecidedAt = now;
        artifact.UpdatedAt = now;

        _dbContext.CoreApprovals.Add(approval);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "artifact.rejected",
            "Approval",
            approval.Id,
            $"Artifact '{artifact.Title}' rejected.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Artifact rejected successfully.", Approval: approval.ToResponse());
    }

    public async Task<CoreActionResponse> RequestRevisionAsync(Guid artifactId, string? comment = null, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.CoreArtifacts
            .Include(x => x.Revisions)
            .SingleOrDefaultAsync(x => x.Id == artifactId, cancellationToken);

        if (artifact is null)
        {
            return Failure("not_found", "Artifact was not found.");
        }

        var now = DateTimeOffset.UtcNow;
        var revision = CurrentOrMigratedRevision(artifact, now);
        var approval = new Approval
        {
            Id = Guid.NewGuid(),
            ArtifactId = artifactId,
            ArtifactRevisionId = revision.Id,
            Status = ApprovalStatus.RevisionRequested,
            Comment = comment,
            DecidedAt = now,
            CreatedAt = now
        };

        artifact.ApprovalStatus = ApprovalStatus.RevisionRequested;
        artifact.SubmittedRevisionId = null;
        artifact.DocumentStatus = ArtifactDocumentStatus.ChangesRequested;
        revision.Status = ArtifactRevisionStatus.Rejected;
        revision.DecidedAt = now;
        artifact.UpdatedAt = now;

        _dbContext.CoreApprovals.Add(approval);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "artifact.revision_requested",
            "Approval",
            approval.Id,
            $"Artifact '{artifact.Title}' revision requested.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Revision requested successfully.", Approval: approval.ToResponse());
    }

    static CoreActionResponse Failure(string errorCode, string message) =>
        new CoreActionResponse(false, errorCode, message);

    private ArtifactRevision CurrentOrMigratedRevision(Artifact artifact, DateTimeOffset now)
    {
        var revision = artifact.Revisions.SingleOrDefault(x => x.Id == artifact.SubmittedRevisionId) ??
            artifact.Revisions.SingleOrDefault(x => x.Id == artifact.LatestRevisionId) ??
            artifact.Revisions.OrderByDescending(x => x.Number).FirstOrDefault();
        if (revision is not null) return revision;

        // Compatibility for callers/tests that still materialize the pre-revision aggregate.
        var content = artifact.Content ?? string.Empty;
        revision = new ArtifactRevision
        {
            Id = Guid.NewGuid(), OrganizationId = artifact.OrganizationId, ArtifactId = artifact.Id,
            Number = Math.Max(artifact.Version, 1), Content = content,
            ContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            Status = ArtifactRevisionStatus.Draft, CreatorDisplayName = artifact.CreatorDisplayName,
            IdempotencyKey = $"legacy-approval:{artifact.Id:D}", CreatedAt = artifact.CreatedAt == default ? now : artifact.CreatedAt
        };
        artifact.LatestRevisionId = revision.Id;
        artifact.Version = revision.Number;
        _dbContext.ArtifactRevisions.Add(revision);
        return revision;
    }
}
