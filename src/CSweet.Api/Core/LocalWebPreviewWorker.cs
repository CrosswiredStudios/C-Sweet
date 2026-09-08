using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Core;

/// <summary>Development-host lifecycle for local static web build previews.</summary>
internal sealed class LocalWebPreviewWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<LocalWebPreviewWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, (LocalWebPreviewServer Server, DateTimeOffset Expires)> running = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogError(error, "Local web preview reconciliation failed."); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally
        {
            foreach (var preview in running.Values) await preview.Server.DisposeAsync();
            running.Clear();
        }
    }

    internal async Task ReconcileAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IAgentArtifactStore>();
        var records = await db.PreviewSessions.AsTracking().Where(x => x.Mode == "web-static" &&
            (x.Status == "Requested" || x.Status == "Ready")).OrderBy(x => x.CreatedAt).ToListAsync(token);
        var activeIds = records.Where(x => x.ExpiresAt > now).Select(x => x.Id).ToHashSet();
        foreach (var id in running.Keys.Where(x => !activeIds.Contains(x)).ToArray())
        {
            await running[id].Server.DisposeAsync();
            running.Remove(id);
        }
        foreach (var record in records)
        {
            if (record.ExpiresAt <= now)
            {
                record.Status = "Expired"; record.AccessReference = null;
                continue;
            }
            if (running.TryGetValue(record.Id, out var existing))
            {
                if (existing.Expires == record.ExpiresAt)
                {
                    record.Status = "Ready"; record.AccessReference = existing.Server.AccessReference;
                    continue;
                }
                await existing.Server.DisposeAsync();
                running.Remove(record.Id);
            }
            if (running.Count >= 3)
            {
                // A restart invalidates old process URLs until a slot can be restored.
                record.Status = "Requested"; record.AccessReference = null;
                continue;
            }
            try
            {
                var build = await db.DeliveryBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == record.BuildId &&
                    x.OrganizationId == record.OrganizationId && x.WorkstreamId == record.WorkstreamId, token)
                    ?? throw new InvalidDataException("The preview build no longer exists.");
                if (build.Status != DeliveryBuildStatuses.Succeeded) throw new InvalidDataException("The preview build is not successful.");
                var artifact = await db.ExecutionWorkloadAssignments.AsNoTracking().Where(x =>
                    x.DeliveryBuildId == build.Id && x.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild && x.ResultArtifactDigest != null)
                    .Select(x => x.ResultArtifactDigest).SingleOrDefaultAsync(token)
                    ?? throw new InvalidDataException("The preview build has no ingested output bundle.");
                var manifest = JsonSerializer.Deserialize<IReadOnlyList<BuildOutputManifestEntry>>(build.OutputsJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                await using var archive = await store.OpenReadAsync(artifact, token);
                var bundle = await WebPreviewBundle.ReadAsync(archive, manifest, token);
                var server = await LocalWebPreviewServer.StartAsync(bundle, record.ExpiresAt, clock, token);
                running.Add(record.Id, (server, record.ExpiresAt));
                record.Status = "Ready"; record.AccessReference = server.AccessReference;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                record.Status = "Failed"; record.AccessReference = null;
                logger.LogWarning(error, "Local web preview {PreviewId} could not start.", record.Id);
            }
        }
        await db.SaveChangesAsync(token);
    }
}
