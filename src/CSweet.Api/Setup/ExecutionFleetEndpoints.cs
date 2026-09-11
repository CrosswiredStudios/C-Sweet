using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Setup;

public static class ExecutionFleetEndpoints
{
    public static async Task<IResult> UpdateOfficeSettingsAsync(Guid nodeId, UpdateOfficeSettingsRequest request,
        CSweetDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || request.Name.Any(char.IsControl))
            return Results.BadRequest(new ExecutionFleetMutationResponse(false, "invalid_name", "Enter an Office name of up to 200 characters."));
        var node = await db.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is null) return Results.NotFound();
        if (node.Status is ExecutionNodeStatus.Revoked or ExecutionNodeStatus.PendingApproval)
            return Results.BadRequest(new ExecutionFleetMutationResponse(false, "office_unavailable", "Approve this Office before changing its settings."));
        var now = clock.GetUtcNow();
        if (await db.LocalOfficeSetupSessions.AnyAsync(x => x.UpgradeOfficeId == nodeId && x.ExpiresAt > now &&
            (x.Status == LocalOfficeSetupSessionStatus.Created || x.Status == LocalOfficeSetupSessionStatus.Redeemed ||
             x.Status == LocalOfficeSetupSessionStatus.RemovalInProgress), cancellationToken))
            return Results.BadRequest(new ExecutionFleetMutationResponse(false, "maintenance_in_progress", "Finish Office maintenance before changing its settings."));
        if (!await db.ExecutionPools.AnyAsync(x => x.Id == request.ExecutionPoolId && x.IsEnabled, cancellationToken))
            return Results.BadRequest(new ExecutionFleetMutationResponse(false, "pool_unavailable", "Choose an enabled workload group."));
        if (node.ExecutionPoolId != request.ExecutionPoolId && await db.ExecutionWorkloadAssignments.AnyAsync(x =>
            x.ExecutionNodeId == nodeId && (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned || x.Status == ExecutionAssignmentStatus.Starting ||
                x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping), cancellationToken))
            return Results.BadRequest(new ExecutionFleetMutationResponse(false, "office_busy", "Let current work finish before changing the workload group."));
        node.Name = request.Name.Trim();
        node.ExecutionPoolId = request.ExecutionPoolId;
        node.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new ExecutionFleetMutationResponse(true, null, "Office settings saved."));
    }

    public static IEndpointRouteBuilder MapExecutionFleetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/execution-fleet")
            .RequireAuthorization("HostAdministration");

        group.MapGet("/", GetAsync);
        group.MapPut("/nodes/{nodeId:guid}/settings", UpdateOfficeSettingsAsync);
        group.MapGet("/nodes/{nodeId:guid}/recovery-package", async (
            Guid nodeId, CSweetDbContext db, IHttpClientFactory clients,
            Microsoft.Extensions.Options.IOptions<CSweet.Infrastructure.Setup.ExecutionFleetOptions> options,
            CancellationToken cancellationToken) =>
        {
            var node = await db.ExecutionNodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
            if (node is null) return Results.NotFound();
            if (!Uri.TryCreate(options.Value.ReleaseManifestUrl, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != "https")
                return Results.Ok(new { url = (string?)null });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var client = clients.CreateClient();
                using var response = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.Scheme != "https") return Results.Ok(new { url = (string?)null });
                await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                return Results.Ok(new { url = FindRecoveryPackage(document.RootElement, node.OperatingSystem, node.Architecture) });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                return Results.Ok(new { url = (string?)null });
            }
        });
        group.MapGet("/nodes/{nodeId:guid}/activity", async (
            Guid nodeId, CSweetDbContext db, CancellationToken cancellationToken) =>
        {
            var activity = await GetOfficeActivityAsync(db, nodeId, cancellationToken);
            return activity is null ? Results.NotFound() : Results.Ok(activity);
        });
        group.MapPost("/pools", async (
            CreateExecutionPoolRequest request,
            IExecutionPoolAdministrationService service,
            CancellationToken cancellationToken) =>
            Mutation(await service.CreatePoolAsync(request, cancellationToken)));
        group.MapPut("/pools/{poolId:guid}", async (
            Guid poolId,
            UpdateExecutionPoolRequest request,
            IExecutionPoolAdministrationService service,
            CancellationToken cancellationToken) =>
            Mutation(await service.UpdatePoolAsync(poolId, request, cancellationToken)));
        group.MapDelete("/pools/{poolId:guid}", async (
            Guid poolId,
            IExecutionPoolAdministrationService service,
            CancellationToken cancellationToken) =>
            Mutation(await service.DeletePoolAsync(poolId, cancellationToken)));
        group.MapPut("/installations/{installationId:guid}/runtime-pool", async (
            Guid installationId,
            UpdateAgentExecutionPoolRequest request,
            IExecutionPoolAdministrationService service,
            CancellationToken cancellationToken) =>
            Mutation(await service.SetInstallationPoolAsync(installationId, request, cancellationToken)));
        group.MapPost("/nodes/{nodeId:guid}/drain", async (
            Guid nodeId, CSweetDbContext db, CancellationToken cancellationToken) =>
            await ChangeStateAsync(nodeId, true, db, cancellationToken));
        group.MapPost("/nodes/{nodeId:guid}/resume", async (
            Guid nodeId, CSweetDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var node = await db.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
            if (node is null) return Results.NotFound();
            if (node.Status == ExecutionNodeStatus.Offline)
                return Results.BadRequest(new ExecutionFleetMutationResponse(false, "office_offline",
                    $"{node.Name} is offline and cannot be started remotely. Start the C-Sweet Office services on {node.MachineName}; it will reconnect automatically."));
            if (node.Status != ExecutionNodeStatus.Draining || node.ApprovedAt is null || node.RevokedAt is not null)
                return Results.BadRequest(new ExecutionFleetMutationResponse(false, "office_not_draining",
                    "Only an approved, deliberately drained Office can be resumed."));
            if (await db.LocalOfficeSetupSessions.AnyAsync(x => x.UpgradeOfficeId == nodeId &&
                (x.RecoveryAction == "upgrade" || x.RecoveryAction == "repair") && x.ExpiresAt > clock.GetUtcNow() &&
                (x.Status == LocalOfficeSetupSessionStatus.Created || x.Status == LocalOfficeSetupSessionStatus.Redeemed), cancellationToken))
                return Results.BadRequest(new ExecutionFleetMutationResponse(false, "office_upgrade_in_progress",
                    "Wait for the Office upgrade to finish before resuming work."));
            node.DrainingAt = null;
            node.Status = node.LastHeartbeatAt >= clock.GetUtcNow().AddSeconds(-30)
                ? ExecutionNodeStatus.Ready : ExecutionNodeStatus.Offline;
            node.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            var message = node.Status == ExecutionNodeStatus.Ready
                ? $"{node.Name} resumed and is ready for work."
                : $"{node.Name} is no longer drained, but it is offline. Start the C-Sweet Office services on {node.MachineName}; it will reconnect automatically.";
            return Results.Ok(new ExecutionFleetMutationResponse(true, null, message));
        });
        group.MapDelete("/nodes/{nodeId:guid}", async (
            Guid nodeId, CSweetDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var node = await db.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
            if (node is null) return Results.NotFound();
            var now = clock.GetUtcNow();
            node.Status = ExecutionNodeStatus.Revoked;
            node.RevokedAt = now;
            node.UpdatedAt = now;
            var active = await db.ExecutionWorkloadAssignments.Where(x => x.ExecutionNodeId == nodeId &&
                (x.Status == ExecutionAssignmentStatus.Assigned || x.Status == ExecutionAssignmentStatus.Starting ||
                 x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping))
                .ToListAsync(cancellationToken);
            foreach (var assignment in active)
            {
                assignment.Status = ExecutionAssignmentStatus.Fenced;
                assignment.FencingEpoch++;
                assignment.LeaseExpiresAt = null;
                assignment.CompletedAt = now;
                assignment.FailureCode = "execution-node-revoked";
                assignment.SanitizedFailure = "The execution node was revoked by an administrator.";
            }
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
        group.MapPut("/nodes/{nodeId:guid}/labels", async (
            Guid nodeId, UpdateExecutionNodeLabelsRequest request, CSweetDbContext db,
            TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (request.Labels.Count > 64 || request.Labels.Any(x =>
                    x.Key.Length is < 1 or > 64 || x.Value.Length > 256 ||
                    x.Key.StartsWith("csweet.security.", StringComparison.Ordinal) ||
                    x.Key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) ||
                    x.Value.Any(char.IsControl)))
                return Results.BadRequest();
            var node = await db.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
            if (node is null) return Results.NotFound();
            Dictionary<string, string> labels;
            try { labels = JsonSerializer.Deserialize<Dictionary<string, string>>(node.LabelsJson) ?? []; }
            catch (JsonException) { labels = []; }
            foreach (var key in labels.Keys.Where(key => !key.StartsWith("csweet.security.", StringComparison.Ordinal)).ToArray())
                labels.Remove(key);
            foreach (var item in request.Labels) labels[item.Key] = item.Value;
            node.LabelsJson = JsonSerializer.Serialize(labels);
            node.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
        return endpoints;
    }

    private static async Task<IResult> ChangeStateAsync(
        Guid nodeId, bool drain, CSweetDbContext db, CancellationToken cancellationToken)
    {
        var node = await db.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is null) return Results.NotFound();
        if (!drain || node.Status != ExecutionNodeStatus.Ready ||
            node.ApprovedAt is null || node.RevokedAt is not null)
            return Results.BadRequest();
        node.Status = drain ? ExecutionNodeStatus.Draining : node.Status;
        node.DrainingAt = drain ? DateTimeOffset.UtcNow : null;
        node.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<ExecutionFleetAdministrationResponse> GetAsync(
        CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var pools = await db.ExecutionPools.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var nodes = await db.ExecutionNodes.AsNoTracking().Include(x => x.Providers)
            .OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var assignments = await db.ExecutionWorkloadAssignments.AsNoTracking()
            .OrderByDescending(x => x.QueuedAt).Take(200).ToListAsync(cancellationToken);
        var activeCounts = await db.ExecutionWorkloadAssignments.AsNoTracking()
            .Where(x => x.Status == ExecutionAssignmentStatus.Pending ||
                x.Status == ExecutionAssignmentStatus.Assigned ||
                x.Status == ExecutionAssignmentStatus.Starting ||
                x.Status == ExecutionAssignmentStatus.Running ||
                x.Status == ExecutionAssignmentStatus.Stopping)
            .GroupBy(x => x.ExecutionPoolId)
            .Select(group => new { PoolId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.PoolId, x => x.Count, cancellationToken);
        var nodeActivity = await ActiveOfficeAssignments(db)
            .GroupBy(x => x.ExecutionNodeId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var settings = await db.AgentRuntimeGlobalSettings.AsNoTracking()
            .OrderBy(x => x.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
        var defaultRuntimePoolId = settings?.DefaultRuntimeExecutionPoolId ??
            pools.Single(x => x.IsDefaultRuntimePool).Id;
        var installations = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion)
            .Where(x => x.RevisionStatus == PluginRevisionStatus.Active)
            .OrderBy(x => x.PackageVersion!.AgentName).ThenBy(x => x.BusinessId)
            .ToListAsync(cancellationToken);
        return new ExecutionFleetAdministrationResponse(
            pools.Select(pool => new ExecutionPoolResponse(
                pool.Id, pool.Name, pool.IsDefaultBuildPool, pool.IsDefaultRuntimePool,
                pool.IsEnabled, pool.MaximumActiveWorkloads,
                nodes.Count(x => x.ExecutionPoolId == pool.Id && x.Status == ExecutionNodeStatus.Ready &&
                    x.ApprovedAt != null && x.DrainingAt == null && x.RevokedAt == null),
                nodes.Count(x => x.ExecutionPoolId == pool.Id),
                activeCounts.GetValueOrDefault(pool.Id),
                DeserializeDictionary(pool.RequiredLabelsJson),
                DeserializeList(pool.AllowedBusinessIdsJson))).ToArray(),
            nodes.Select(Map).ToArray(),
            assignments.Select(x => new ExecutionAssignmentSummaryResponse(
                x.Id, x.ExecutionPoolId, x.ExecutionNodeId, x.AgentBuildJobId,
                x.AgentRuntimeInstanceId, x.WorkloadKind.ToString().ToLowerInvariant(),
                x.Status.ToString().ToLowerInvariant(), x.ProviderId, x.GuestImageDigest,
                x.Attempt, x.FencingEpoch, x.ReservedCpuCount, x.ReservedMemoryMb,
                x.ReservedDiskMb, x.QueuedAt, x.AssignedAt, x.StartedAt, x.CompletedAt,
                x.FailureCode)).ToArray(),
            installations.Select(installation =>
            {
                var effectivePoolId = installation.ExecutionPoolId ?? defaultRuntimePoolId;
                return new AgentExecutionPoolOverrideResponse(
                    installation.Id,
                    installation.PackageVersion?.AgentName ?? installation.Id.ToString("D"),
                    installation.BusinessId,
                    installation.ExecutionPoolId,
                    effectivePoolId,
                    pools.Single(pool => pool.Id == effectivePoolId).Name);
            }).ToArray(),
            nodes.ToDictionary(node => node.Id, node => nodeActivity.GetValueOrDefault(node.Id)));
    }

    private static IQueryable<ExecutionWorkloadAssignment> ActiveOfficeAssignments(CSweetDbContext db) =>
        db.ExecutionWorkloadAssignments.AsNoTracking().Where(x => x.ExecutionNodeId != null &&
            (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned ||
             x.Status == ExecutionAssignmentStatus.Starting || x.Status == ExecutionAssignmentStatus.Running ||
             x.Status == ExecutionAssignmentStatus.Stopping));

    internal static async Task<OfficeActivityResponse?> GetOfficeActivityAsync(
        CSweetDbContext db, Guid nodeId, CancellationToken cancellationToken = default)
    {
        if (!await db.ExecutionNodes.AsNoTracking().AnyAsync(x => x.Id == nodeId, cancellationToken)) return null;
        var active = await ActiveOfficeAssignments(db).CountAsync(x => x.ExecutionNodeId == nodeId, cancellationToken);
        var recent = await db.ExecutionWorkloadAssignments.AsNoTracking().Where(x => x.ExecutionNodeId == nodeId)
            .OrderByDescending(x => x.QueuedAt).Take(50).ToListAsync(cancellationToken);
        return new(active, recent.Select(x => new ExecutionAssignmentSummaryResponse(
            x.Id, x.ExecutionPoolId, x.ExecutionNodeId, x.AgentBuildJobId, x.AgentRuntimeInstanceId,
            x.WorkloadKind.ToString().ToLowerInvariant(), x.Status.ToString().ToLowerInvariant(),
            x.ProviderId, x.GuestImageDigest, x.Attempt, x.FencingEpoch, x.ReservedCpuCount,
            x.ReservedMemoryMb, x.ReservedDiskMb, x.QueuedAt, x.AssignedAt, x.StartedAt, x.CompletedAt,
            x.FailureCode)).ToArray());
    }

    private static IResult Mutation(ExecutionFleetMutationResponse result) =>
        result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);

    private static IReadOnlyDictionary<string, string> DeserializeDictionary(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static string? FindRecoveryPackage(JsonElement manifest, string os, string architecture)
    {
        if (!manifest.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1 ||
            !manifest.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            string? Text(string name) => asset.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!string.Equals(Text("operatingSystem"), os, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Text("architecture"), architecture, StringComparison.OrdinalIgnoreCase) ||
                Text("sha256") is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) ||
                !Uri.TryCreate(Text("url"), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) continue;
            if (os == "windows" && Text("packageType") != "msi") continue;
            return uri.AbsoluteUri;
        }
        return null;
    }

    private static ExecutionNodeSummaryResponse Map(ExecutionNode node) => new(
        node.Id, node.ExecutionPoolId, node.Name, node.MachineName, node.OperatingSystem,
        node.Architecture, node.NodeVersion, node.ProtocolVersion,
        node.Status.ToString().ToLowerInvariant(), node.CertificateThumbprint,
        node.CertificateExpiresAt, node.AllocatableCpuCount, node.AllocatableMemoryMb,
        node.AllocatableDiskMb, node.MaximumConcurrentWorkloads, node.LastHeartbeatAt,
        node.Providers.Select(provider => new ExecutionNodeProviderResponse(
            provider.ProviderId, provider.ProviderVersion, provider.BrokerProtocolVersion,
            provider.GuestImageDigest, provider.CertificationSuiteVersion,
            provider.CertificationEvidenceDigest, provider.CertifiedAt,
            provider.CertificationExpiresAt, provider.SupportsBuilderWorkloads,
            provider.SupportsRuntimeWorkloads, provider.SupportsToolchainBuildWorkloads,
            provider.IsAvailable, provider.UnavailableReason)).ToArray(),
        DeserializeDictionary(node.LabelsJson),
        string.Equals(node.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(node.OperatingSystem, OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos", StringComparison.OrdinalIgnoreCase));
}
