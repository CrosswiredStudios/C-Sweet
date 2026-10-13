using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

/// <summary>Stages review completion and notification resolution for the caller's atomic save.</summary>
public static class ArtifactReviewJobLifecycle
{
    public const string ReassignmentCategory = "ArtifactReviewReassignment";

    public static async Task CompleteRevisionAsync(CSweetDbContext db, Guid organizationId, Guid revisionId,
        DateTimeOffset now, CancellationToken token)
    {
        foreach (var job in await db.ArtifactReviewJobs.Where(x => x.OrganizationId == organizationId &&
                     x.RevisionId == revisionId).ToListAsync(token))
        {
            job.Status = ArtifactReviewJobStatus.Completed;
            job.LastError = null;
            await DismissNotificationsAsync(db, job, now, token);
        }
    }

    public static async Task DismissNotificationsAsync(CSweetDbContext db, ArtifactReviewJob job,
        DateTimeOffset now, CancellationToken token)
    {
        var prefix = $"artifact-review-reassignment:{job.Id:N}:";
        foreach (var notification in await db.UserNotifications.Where(x => x.OrganizationId == job.OrganizationId &&
                     x.Category == ReassignmentCategory && x.DismissedAt == null &&
                     x.DeduplicationKey != null && x.DeduplicationKey.StartsWith(prefix)).ToListAsync(token))
            notification.DismissedAt = now;
    }
}
