using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Analytics;

/// <summary>Platform-owned attribution. Request payloads never supply an execution grant.</summary>
public static class InferenceAttribution
{
    public static async Task CaptureAsync(CSweetDbContext db, AgentRunLog log, Guid? workId,
        CancellationToken ct = default)
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

        if (workId is { } id)
        {
            var work = await db.AgentWorkItems.SingleOrDefaultAsync(x => x.Id == id &&
                x.AgentInstallationId == log.AgentInstallationId && x.OrganizationId == organizationId.ToString(), ct);
            if (work is not null)
            {
                log.AgentWorkItemId = work.Id;
                var item = await AgentTicketFeedback.ResolveAsync(db, work, ct);
                if (item is not null)
                {
                    log.WorkItemId = item.Id;
                    log.WorkstreamId = item.Board?.WorkstreamId;
                    log.AttributionKind = "Execution";

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
        if (log.WorkItemId is { } itemId)
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
        if (log.WorkItemId is null) log.AttributionKind = "BusinessOverhead";
    }
}
