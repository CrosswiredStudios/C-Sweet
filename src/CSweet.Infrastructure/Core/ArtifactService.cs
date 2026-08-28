using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Core;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed class ArtifactService : IArtifactService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAuditEventWriter _auditEventWriter;

    public ArtifactService(CSweetDbContext dbContext, IAuditEventWriter auditEventWriter)
    {
        _dbContext = dbContext;
        _auditEventWriter = auditEventWriter;
    }

    public async Task<IReadOnlyList<ArtifactResponse>> ListByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.CoreArtifacts
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ToResponse())
            .ToListAsync(cancellationToken);
    }

    public async Task<ArtifactResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.CoreArtifacts
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        return artifact?.ToResponse();
    }

    public async Task<CoreActionResponse> CreateAsync(Guid organizationId, CreateArtifactRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _dbContext.CoreOrganizations.AnyAsync(x => x.Id == organizationId, cancellationToken))
        {
            return Failure("organization_not_found", "Organization was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return Failure("validation_error", "Artifact title is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Failure("validation_error", "Artifact content is required.");
        }

        var now = DateTimeOffset.UtcNow;
        var content = request.Content.Trim();
        var revisionId = Guid.NewGuid();
        var approvalStatus = (ApprovalStatus)request.ApprovalStatus;
        var revisionStatus = approvalStatus switch
        {
            ApprovalStatus.Approved => ArtifactRevisionStatus.Accepted,
            ApprovalStatus.Rejected => ArtifactRevisionStatus.Rejected,
            _ => ArtifactRevisionStatus.Draft
        };
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            TaskId = request.TaskId,
            TaskRunId = request.TaskRunId,
            Type = (ArtifactType)request.Type,
            Title = request.Title.Trim(),
            Content = content,
            Version = request.Version > 0 ? request.Version : 1,
            ApprovalStatus = approvalStatus,
            CreatorDisplayName = "Compatibility API",
            DocumentType = $"legacy.{((ArtifactType)request.Type).ToString().ToLowerInvariant()}",
            DocumentStatus = approvalStatus == ApprovalStatus.Approved
                ? ArtifactDocumentStatus.Approved
                : ArtifactDocumentStatus.Draft,
            LatestRevisionId = revisionId,
            AcceptedRevisionId = approvalStatus == ApprovalStatus.Approved ? revisionId : null,
            CreatedAt = now,
            UpdatedAt = now
        };
        var revision = new ArtifactRevision
        {
            Id = revisionId,
            OrganizationId = organizationId,
            ArtifactId = artifact.Id,
            Number = artifact.Version,
            Content = content,
            ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            Status = revisionStatus,
            CreatorDisplayName = artifact.CreatorDisplayName,
            IdempotencyKey = $"legacy-create:{artifact.Id:D}",
            CreatedAt = now,
            SubmittedAt = revisionStatus is ArtifactRevisionStatus.Accepted or ArtifactRevisionStatus.Rejected ? now : null,
            DecidedAt = revisionStatus is ArtifactRevisionStatus.Accepted or ArtifactRevisionStatus.Rejected ? now : null
        };

        _dbContext.CoreArtifacts.Add(artifact);
        _dbContext.ArtifactRevisions.Add(revision);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "artifact.created",
            "Artifact",
            artifact.Id,
            $"Artifact '{artifact.Title}' created.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Artifact created successfully.", Artifact: artifact.ToResponse());
    }

    public async Task<CoreActionResponse> UpdateAsync(Guid id, UpdateArtifactRequest request, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.CoreArtifacts
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (artifact is null)
        {
            return Failure("not_found", "Artifact was not found.");
        }

        // Prevent updating approved artifacts without creating a new version
        if (artifact.ApprovalStatus == ApprovalStatus.Approved)
        {
            return Failure("approval_conflict", "Approved artifact cannot be updated. Create a new version instead.");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
            artifact.Title = request.Title.Trim();
        if (!string.IsNullOrEmpty(request.Content))
        {
            var content = request.Content.Trim();
            var currentRevision = await _dbContext.ArtifactRevisions
                .Where(x => x.ArtifactId == artifact.Id)
                .OrderByDescending(x => x.Number)
                .FirstOrDefaultAsync(cancellationToken);
            var revisionNumber = request.Version is > 0
                ? Math.Max(request.Version.Value, (currentRevision?.Number ?? 0) + 1)
                : (currentRevision?.Number ?? artifact.Version) + 1;
            var revision = new ArtifactRevision
            {
                Id = Guid.NewGuid(), OrganizationId = artifact.OrganizationId, ArtifactId = artifact.Id,
                Number = revisionNumber, BaseRevisionId = artifact.LatestRevisionId,
                Content = content,
                ContentSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                Status = ArtifactRevisionStatus.Draft, CreatorDisplayName = "Compatibility API",
                IdempotencyKey = $"legacy-update:{artifact.Id:D}:{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow
            };
            _dbContext.ArtifactRevisions.Add(revision);
            artifact.Content = content;
            artifact.Version = revisionNumber;
            artifact.LatestRevisionId = revision.Id;
            artifact.SubmittedRevisionId = null;
            artifact.DocumentStatus = ArtifactDocumentStatus.Draft;
        }
        if (request.ApprovalStatus.HasValue)
            artifact.ApprovalStatus = (ApprovalStatus)request.ApprovalStatus.Value;

        artifact.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "artifact.updated",
            "Artifact",
            artifact.Id,
            $"Artifact '{artifact.Title}' updated.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Artifact updated successfully.", Artifact: artifact.ToResponse());
    }

    public async Task<CoreActionResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.CoreArtifacts
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (artifact is null)
        {
            return Failure("not_found", "Artifact was not found.");
        }

        var title = artifact.Title;
        artifact.ArchivedAt = DateTimeOffset.UtcNow;
        artifact.UpdatedAt = artifact.ArchivedAt.Value;
        artifact.DocumentStatus = ArtifactDocumentStatus.Archived;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditEventWriter.WriteAsync(
            "artifact.archived",
            "Artifact",
            artifact.Id,
            $"Artifact '{title}' archived through the compatibility API.",
            cancellationToken: cancellationToken);

        return new CoreActionResponse(true, null, "Artifact archived successfully.");
    }

    static CoreActionResponse Failure(string errorCode, string message) =>
        new CoreActionResponse(false, errorCode, message);
}
