using CSweet.Application.Core;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Chat;

/// <summary>Serializes submitted document reviews behind any active conversation turn.</summary>
public sealed class ArtifactReviewJobWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ArtifactReviewJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Document review queue processing failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var turns = scope.ServiceProvider.GetRequiredService<IChatTurnService>();
        var now = clock.GetUtcNow();
        var job = await db.ArtifactReviewJobs.OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.Status == ArtifactReviewJobStatus.Pending && x.NextAttemptAt <= now &&
                !db.ArtifactReviewJobs.Any(earlier => earlier.Status == ArtifactReviewJobStatus.Pending &&
                    earlier.CreatedAt < x.CreatedAt && earlier.ConversationId == x.ConversationId &&
                    earlier.ReviewerInstallationId == x.ReviewerInstallationId), token);
        if (job is null) return false;
        if (!job.ConversationId.HasValue || !job.ReviewerOrganizationUserId.HasValue)
        {
            job.LastError = "An active agent reviewer and conversation must be assigned.";
            job.NextAttemptAt = now.AddMinutes(15);
            await NotifyReassignmentAsync(db, job, now, token);
            await db.SaveChangesAsync(token);
            return true;
        }
        var reviewer = await db.CoreOrganizationUsers.AsNoTracking().Where(x =>
                x.Id == job.ReviewerOrganizationUserId.Value && x.OrganizationId == job.OrganizationId && x.IsActive)
            .Select(x => new { x.EmployeeType, x.AgentInstallationId })
            .SingleOrDefaultAsync(token);
        if (reviewer?.EmployeeType == EmployeeType.Human)
        {
            job.Status = ArtifactReviewJobStatus.Completed;
            job.LastError = null;
            await db.SaveChangesAsync(token);
            return true;
        }
        if (reviewer?.AgentInstallationId is not Guid reviewerInstallationId)
        {
            job.LastError = "An active agent reviewer and conversation must be assigned.";
            job.NextAttemptAt = now.AddMinutes(15);
            await NotifyReassignmentAsync(db, job, now, token);
            await db.SaveChangesAsync(token);
            return true;
        }
        job.ReviewerInstallationId = reviewerInstallationId;

        var artifact = await db.CoreArtifacts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == job.ArtifactId && x.OrganizationId == job.OrganizationId, token);
        var revision = await db.ArtifactRevisions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == job.RevisionId && x.ArtifactId == job.ArtifactId, token);
        if (artifact is null || revision is null)
        {
            job.Status = ArtifactReviewJobStatus.Failed;
            job.LastError = "The submitted document or revision no longer exists.";
            await db.SaveChangesAsync(token);
            return true;
        }
        if (artifact.CreatedByOrganizationUserId == job.ReviewerOrganizationUserId)
        {
            job.Status = ArtifactReviewJobStatus.Completed;
            job.LastError = null;
            await db.SaveChangesAsync(token);
            return true;
        }

        try
        {
            var started = await turns.StartForAgentAsync(job.OrganizationId, job.ConversationId.Value,
                job.ReviewerOrganizationUserId.Value,
                $"A document revision was submitted for your review. Use get_artifact with artifactId {artifact.Id:D}, " +
                $"review exact revision {revision.Id:D} (revision {revision.Number}), and comment, ask questions, or use " +
                "decide_artifact_revision. Do not assume approval from this notification.",
                senderOrganizationUserId: artifact.CreatedByOrganizationUserId,
                sourceProvider: CommunicationMessageTypes.SystemAction,
                idempotencyKey: $"artifact-review:{job.Id:D}", cancellationToken: token);
            if (started is null)
            {
                job.Attempts++; job.LastError = "The reviewer is unavailable."; job.NextAttemptAt = now.AddMinutes(5);
                await NotifyReassignmentAsync(db, job, now, token);
                await db.SaveChangesAsync(token); return true;
            }
            db.ConversationMessageArtifacts.Add(new ConversationMessageArtifact
            {
                Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, ConversationId = job.ConversationId.Value,
                MessageId = started.UserMessage.Id, ArtifactId = artifact.Id, RevisionId = revision.Id, CreatedAt = now
            });
            job.Status = ArtifactReviewJobStatus.Completed; job.Attempts++; job.LastError = null;
            await db.SaveChangesAsync(token);
            return true;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("active turn", StringComparison.OrdinalIgnoreCase))
        {
            job.Attempts++; job.LastError = "Waiting for the active reviewer turn."; job.NextAttemptAt = now.AddSeconds(5);
            await db.SaveChangesAsync(token); return true;
        }
    }

    private static async Task NotifyReassignmentAsync(CSweetDbContext db, ArtifactReviewJob job,
        DateTimeOffset now, CancellationToken token)
    {
        var recipients = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == job.OrganizationId && x.IsActive &&
                x.EmployeeType == EmployeeType.Human && x.PermissionLevel >= OrganizationPermissionLevel.Manager)
            .Select(x => x.Id).ToListAsync(token);
        foreach (var recipient in recipients)
        {
            var key = $"artifact-review-reassignment:{job.Id:N}:{recipient:N}";
            if (await db.UserNotifications.AsNoTracking().AnyAsync(x =>
                    x.OrganizationId == job.OrganizationId && x.DeduplicationKey == key, token))
                continue;
            db.UserNotifications.Add(new UserNotification
            {
                Id = Guid.NewGuid(), OrganizationId = job.OrganizationId,
                RecipientOrganizationUserId = recipient, Severity = NotificationSeverity.Important,
                Category = "ArtifactReviewReassignment", Title = "Document review needs reassignment",
                Body = "A submitted document is waiting because its assigned agent reviewer is unavailable.",
                ActionUri = $"/organizations/{job.OrganizationId:D}/documents?artifact={job.ArtifactId:D}",
                DeduplicationKey = key, CreatedAt = now
            });
        }
    }
}
