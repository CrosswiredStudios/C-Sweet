using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

/// <summary>Platform-owned attribution. Request payloads never supply an execution grant.</summary>
public static class InferenceAttribution
{
    public static async Task CaptureAsync(CSweetDbContext db, AgentRunLog log, Guid? workId,
        CancellationToken ct = default, int? attemptNumber = null)
    {
        if (log.OrganizationId is not { } organizationId) return;
        log.BenchmarkTrialId ??= await db.BenchmarkTrials.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (log.AgentInstallationId is { } installationId)
        {
            var installation = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
                .Include(x => x.Configuration).Include(x => x.AgentDefinition)!.ThenInclude(x => x!.Configuration)
                .SingleOrDefaultAsync(x => x.Id == installationId && x.BusinessId == organizationId.ToString(), ct);
            if (installation is not null)
            {
                log.AgentPackageVersion = installation.PackageVersion?.Version;
                var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(installation.AgentDefinition?.Configuration?.SettingsJson ?? "{}") ?? [];
                foreach (var setting in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(installation.Configuration?.SettingsJson ?? "{}") ?? [])
                    settings[setting.Key] = setting.Value;
                await BenchmarkModelPolicy.ApplyAsync(db, installationId, installation.BusinessId, settings, ct);
                log.ConfigurationDigest = Setup.AgentConfigurationRules.Digest(settings);
            }
        }

        var capturedAncestry = false;
        if (workId is { } id)
        {
            var work = await db.AgentWorkItems.SingleOrDefaultAsync(x => x.Id == id &&
                x.AgentInstallationId == log.AgentInstallationId && x.OrganizationId == organizationId.ToString(), ct);
            if (work is not null)
            {
                log.AgentWorkItemId = work.Id;
                var dispatchedAt = log.ProviderStartedAt ?? DateTimeOffset.UtcNow;
                var attempts = await db.AgentWorkAttempts.AsNoTracking().Where(x => x.AgentWorkItemId == work.Id &&
                    x.FinishedAt == null && x.LeaseExpiresAt > dispatchedAt &&
                    (!attemptNumber.HasValue || x.Attempt == attemptNumber)).Take(2).ToListAsync(ct);
                if (work.Status == AgentWorkStatus.Leased && attempts.Count == 1)
                {
                    var attempt = attempts[0];
                    log.AgentWorkAttemptId = attempt.Id;
                    var focus = await db.WorkExecutionContexts.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.Id == attempt.Id && x.OrganizationId == organizationId &&
                        x.AgentInstallationId == work.AgentInstallationId, ct);
                    if (focus?.WorkItemId is { } focusId)
                    {
                        var item = await db.CoreWorkTasks.AsNoTracking().Include(x => x.Board).SingleOrDefaultAsync(x =>
                            x.Id == focusId && x.OrganizationId == organizationId && x.ArchivedAt == null, ct);
                        var root = focus.RootWorkItemId is { } rootId ? await db.CoreWorkTasks.AsNoTracking()
                            .Include(x => x.Board).SingleOrDefaultAsync(x => x.Id == rootId && x.OrganizationId == organizationId, ct) : null;
                        var interval = await db.WorkExecutionIntervals.AsNoTracking().SingleOrDefaultAsync(x =>
                            x.AgentWorkAttemptId == attempt.Id && x.EndedAt == null && x.WorkItemId == focusId &&
                            x.OrganizationId == organizationId, ct);
                        var validClaim = root?.Board?.Kind != WorkBoardKind.Personal ||
                            root.Status == WorkTaskStatus.Running && root.ClaimExpiresAt > dispatchedAt &&
                            root.ClaimEventId?.ToString("D") == work.SourceId && root.NextReviewAt is null &&
                            root.AssignedAgentInstallationId == work.AgentInstallationId;
                        if (item is not null && root is { ArchivedAt: null } && root.Board is { ArchivedAt: null } && interval is not null && validClaim)
                        {
                            log.WorkItemId = item.Id;
                            log.WorkstreamId = interval.WorkstreamId;
                            log.AncestorWorkItemIdsJson = interval.AncestorWorkItemIdsJson;
                            capturedAncestry = true;
                            log.AttributionKind = "Execution";
                        }
                    }
                }
            }
        }
        // TaskRun links are an authoritative fallback for platform-hosted workflows.
        if (log.WorkItemId is null && log.TaskRunId is { } taskRunId)
        {
            var run = await db.CoreTaskRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == taskRunId, ct);
            if (run is not null)
            {
                var item = await db.CoreWorkTasks.Include(x => x.Board).SingleOrDefaultAsync(x =>
                    x.Id == run.TaskId && x.OrganizationId == organizationId, ct);
                if (item is not null)
                {
                    log.WorkItemId = item.Id; log.WorkstreamId = item.Board?.WorkstreamId;
                    log.AttributionKind = "TaskRun";
                }
            }
        }
        if (!capturedAncestry && log.WorkItemId is { } itemId)
        {
            var parent = await db.CoreWorkTasks.Where(x => x.Id == itemId && x.OrganizationId == organizationId)
                .Select(x => x.ParentWorkTaskId).SingleOrDefaultAsync(ct);
            var ancestors = new List<Guid>(); var seen = new HashSet<Guid> { itemId };
            while (parent is { } parentId && seen.Add(parentId) && ancestors.Count < 64)
            {
                var row = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == parentId && x.OrganizationId == organizationId, ct);
                if (row is null) break;
                ancestors.Add(row.Id); parent = row.ParentWorkTaskId;
            }
            log.AncestorWorkItemIdsJson = JsonSerializer.Serialize(ancestors);
        }
        if (log.WorkItemId is null) log.AttributionKind = workId.HasValue ? "Unknown" : "BusinessOverhead";
    }
}
