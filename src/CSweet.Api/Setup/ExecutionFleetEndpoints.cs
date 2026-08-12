using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Api.Setup;

public static class ExecutionFleetEndpoints
{
    public static IEndpointRouteBuilder MapExecutionFleetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/execution-fleet")
            .RequireAuthorization("HostAdministration");

        group.MapGet("/", GetAsync);
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
            if (node.Status is not (ExecutionNodeStatus.Draining or ExecutionNodeStatus.Offline) ||
                node.ApprovedAt is null || node.RevokedAt is not null)
                return Results.BadRequest();
            node.DrainingAt = null;
            node.Status = node.LastHeartbeatAt >= clock.GetUtcNow().AddSeconds(-30)
                ? ExecutionNodeStatus.Ready : ExecutionNodeStatus.Offline;
            node.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
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
                    x.Key.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) ||
                    x.Value.Any(char.IsControl)))
                return Results.BadRequest();
            var node = await db.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
            if (node is null) return Results.NotFound();
            node.LabelsJson = JsonSerializer.Serialize(request.Labels);
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
            }).ToArray());
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
            provider.SupportsRuntimeWorkloads, provider.IsAvailable, provider.UnavailableReason)).ToArray(),
        DeserializeDictionary(node.LabelsJson));
}
