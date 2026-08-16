using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Security;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionWorkloadOrchestrator(
    CSweetDbContext dbContext,
    TimeProvider timeProvider) : IExecutionWorkloadOrchestrator
{
    private static readonly TimeSpan AssignmentLease = TimeSpan.FromSeconds(60);
    private const int MaximumUnacknowledgedDeliveryAttempts = 3;
    private static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(30);
    private static readonly ExecutionAssignmentStatus[] ActiveStatuses =
    [
        ExecutionAssignmentStatus.Assigned,
        ExecutionAssignmentStatus.Starting,
        ExecutionAssignmentStatus.Running,
        ExecutionAssignmentStatus.Stopping
    ];

    public async Task<ExecutionWorkloadReference> SubmitAsync(
        ExecutionWorkloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var poolId = request.ExecutionPoolId ?? await DefaultPoolIdAsync(request.WorkloadKind, cancellationToken);
        var existing = request.WorkloadKind == ExecutionWorkloadKind.Builder
            ? await dbContext.ExecutionWorkloadAssignments.FirstOrDefaultAsync(
                x => x.AgentBuildJobId == request.AgentBuildJobId &&
                    (x.Status == ExecutionAssignmentStatus.Pending || ActiveStatuses.Contains(x.Status)), cancellationToken)
            : await dbContext.ExecutionWorkloadAssignments.FirstOrDefaultAsync(
                x => x.AgentRuntimeInstanceId == request.AgentRuntimeInstanceId &&
                    (x.Status == ExecutionAssignmentStatus.Pending || ActiveStatuses.Contains(x.Status)), cancellationToken);
        if (existing is not null)
            return new ExecutionWorkloadReference(existing.Id, existing.FencingEpoch);

        var now = timeProvider.GetUtcNow();
        var specificationJson = BindSecurityPolicy(request.SpecificationJson, request.AllowDevelopmentSecurityPosture);
        var assignment = new ExecutionWorkloadAssignment
        {
            Id = Guid.NewGuid(),
            ExecutionPoolId = poolId,
            AgentBuildJobId = request.AgentBuildJobId,
            AgentRuntimeInstanceId = request.AgentRuntimeInstanceId,
            BusinessId = Bound(request.BusinessId, 128),
            WorkloadKind = request.WorkloadKind,
            Status = ExecutionAssignmentStatus.Pending,
            ProviderId = request.PreferredProviderId ?? string.Empty,
            GuestImageDigest = request.GuestImageDigest,
            ArtifactDigest = request.ArtifactDigest,
            SpecificationJson = specificationJson,
            SpecificationDigest = AssignmentEnvelope.Digest(specificationJson),
            AssignmentTokenHash = HashToken(),
            ReservedCpuCount = request.CpuCount,
            ReservedMemoryMb = request.MemoryMb,
            ReservedDiskMb = request.DiskMb,
            QueuedAt = now
        };
        dbContext.ExecutionWorkloadAssignments.Add(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ExecutionWorkloadReference(assignment.Id, assignment.FencingEpoch);
    }

    public async Task<int> AssignPendingAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var pendingIds = await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .Where(x => x.Status == ExecutionAssignmentStatus.Pending)
            .OrderBy(x => x.QueuedAt)
            .Take(100)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var assigned = 0;
        var postgres = string.Equals(
            dbContext.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);
        foreach (var assignmentId in pendingIds)
        {
            await using var transaction = postgres
                ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;
            try
            {
                var assignment = postgres
                    ? await dbContext.ExecutionWorkloadAssignments
                        .FromSqlInterpolated($"SELECT * FROM \"ExecutionWorkloadAssignments\" WHERE \"Id\" = {assignmentId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken)
                    : await dbContext.ExecutionWorkloadAssignments
                        .SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken);
                if (assignment?.Status != ExecutionAssignmentStatus.Pending)
                {
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    continue;
                }
                if (postgres)
                    _ = await dbContext.ExecutionPools
                        .FromSqlInterpolated($"SELECT * FROM \"ExecutionPools\" WHERE \"Id\" = {assignment.ExecutionPoolId} FOR UPDATE")
                        .SingleAsync(cancellationToken);
                var node = await SelectNodeAsync(assignment, now, cancellationToken);
                if (node is null)
                {
                    if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                    continue;
                }
                assignment.ExecutionNodeId = node.Node.Id;
                assignment.ProviderId = node.Provider.ProviderId;
                assignment.Status = ExecutionAssignmentStatus.Assigned;
                assignment.AssignedAt = now;
                assignment.LeaseExpiresAt = now.Add(AssignmentLease);
                assignment.FencingEpoch++;
                assignment.AssignmentTokenHash = HashToken();
                node.Node.LastAssignedAt = now;
                node.Node.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
                if (transaction is not null) await transaction.CommitAsync(cancellationToken);
                assigned++;
            }
            catch (DbUpdateConcurrencyException)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
            }
        }
        return assigned;
    }

    public async Task<IReadOnlyList<ExecutionAssignmentLease>> GetNodeAssignmentsAsync(
        Guid nodeId,
        long sessionEpoch,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var node = await dbContext.ExecutionNodes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is null || node.Status != ExecutionNodeStatus.Ready ||
            node.ApprovedAt is null || node.DrainingAt is not null || node.RevokedAt is not null ||
            node.SessionEpoch != sessionEpoch || node.LastHeartbeatAt < now.Subtract(HeartbeatFreshness))
            return [];
        var assignments = await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .Where(x => x.ExecutionNodeId == nodeId && ActiveStatuses.Contains(x.Status) && x.LeaseExpiresAt > now)
            .OrderBy(x => x.AssignedAt)
            .ToListAsync(cancellationToken);
        return assignments.Select(x => new ExecutionAssignmentLease(
                x.Id, nodeId, x.FencingEpoch, x.WorkloadKind, x.ProviderId,
                x.GuestImageDigest, x.ArtifactDigest, x.SpecificationJson,
                AssignmentEnvelope.Digest(x.SpecificationJson), x.LeaseExpiresAt!.Value))
            .ToList();
    }

    public async Task<string?> IssueArtifactReadGrantAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var assignment = await dbContext.ExecutionWorkloadAssignments.SingleOrDefaultAsync(x =>
            x.Id == assignmentId && x.ExecutionNodeId == nodeId &&
            x.FencingEpoch == fencingEpoch && x.ArtifactDigest != null &&
            ActiveStatuses.Contains(x.Status) && x.LeaseExpiresAt > now,
            cancellationToken);
        if (assignment is null) return null;
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        assignment.AssignmentTokenHash = Hash(token);
        assignment.ArtifactGrantTransferHash = null;
        assignment.ArtifactGrantInUseUntil = null;
        assignment.ArtifactGrantConsumedAt = null;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return token;
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbContext.Entry(assignment).ReloadAsync(cancellationToken);
            return null;
        }
    }

    public async Task<bool> RenewLeaseAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var assignment = await dbContext.ExecutionWorkloadAssignments
            .Include(x => x.ExecutionNode)
            .SingleOrDefaultAsync(x => x.Id == assignmentId && x.ExecutionNodeId == nodeId, cancellationToken);
        if (assignment is null || assignment.FencingEpoch != fencingEpoch ||
            !ActiveStatuses.Contains(assignment.Status) || assignment.LeaseExpiresAt <= now ||
            assignment.ExecutionNode?.Status != ExecutionNodeStatus.Ready ||
            assignment.ExecutionNode.ApprovedAt is null || assignment.ExecutionNode.DrainingAt is not null ||
            assignment.ExecutionNode.RevokedAt is not null ||
            assignment.ExecutionNode.LastHeartbeatAt < now.Subtract(HeartbeatFreshness))
            return false;
        assignment.LeaseExpiresAt = now.Add(AssignmentLease);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReportStatusAsync(
        Guid nodeId,
        Guid assignmentId,
        long fencingEpoch,
        ExecutionAssignmentStatus status,
        string? failureCode,
        string? sanitizedFailure,
        ExecutionWorkloadResult? result,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var assignment = await dbContext.ExecutionWorkloadAssignments
                .SingleOrDefaultAsync(x => x.Id == assignmentId && x.ExecutionNodeId == nodeId, cancellationToken);
            if (assignment is null || assignment.FencingEpoch != fencingEpoch ||
                assignment.LeaseExpiresAt <= timeProvider.GetUtcNow() || !CanTransition(assignment.Status, status))
                return false;
            var now = timeProvider.GetUtcNow();
            assignment.Status = status;
            if (status == ExecutionAssignmentStatus.Running) assignment.StartedAt ??= now;
            if (status is ExecutionAssignmentStatus.Completed or ExecutionAssignmentStatus.Failed or ExecutionAssignmentStatus.Cancelled)
            {
                assignment.CompletedAt = now;
                assignment.LeaseExpiresAt = null;
            }
            assignment.FailureCode = Bound(failureCode, 128);
            assignment.SanitizedFailure = Bound(sanitizedFailure, 2048);
            if (result is not null)
            {
                assignment.ProviderInstanceId = Bound(result.ProviderInstanceId, 256);
                if (!string.IsNullOrWhiteSpace(result.LogExcerpt))
                    assignment.ResultLogExcerpt = Bound(result.LogExcerpt, 64 * 1024);
            }
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Artifact-grant consumption deliberately rotates concurrency-protected token fields
                // in another request scope. Reload once so an otherwise valid Node status cannot tear
                // down the long-lived control stream merely because that security state changed.
                dbContext.Entry(assignment).State = EntityState.Detached;
            }
        }
        return false;
    }

    public async Task<bool> CancelAsync(
        Guid assignmentId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var assignment = await dbContext.ExecutionWorkloadAssignments
            .SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken);
        if (assignment is null || !assignment.IsActive) return false;
        assignment.Status = ExecutionAssignmentStatus.Cancelled;
        assignment.CompletedAt = timeProvider.GetUtcNow();
        assignment.LeaseExpiresAt = null;
        assignment.FencingEpoch++;
        assignment.FailureCode = "control-plane-cancelled";
        assignment.SanitizedFailure = Bound(reason, 2048);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> FenceExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var expired = await dbContext.ExecutionWorkloadAssignments
            .Include(x => x.ExecutionNode)
            .Where(x => ActiveStatuses.Contains(x.Status) && x.LeaseExpiresAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var assignment in expired)
        {
            if (assignment.Status == ExecutionAssignmentStatus.Assigned &&
                assignment.Attempt >= MaximumUnacknowledgedDeliveryAttempts)
            {
                var node = assignment.ExecutionNode;
                var nodeDescription = node is null
                    ? assignment.ExecutionNodeId?.ToString("D") ?? "unknown"
                    : $"{node.MachineName} ({node.Id:D}, version {node.NodeVersion}, session {node.SessionEpoch})";
                assignment.Status = ExecutionAssignmentStatus.Failed;
                assignment.CompletedAt = now;
                assignment.LeaseExpiresAt = null;
                assignment.FencingEpoch++;
                assignment.FailureCode = "assignment-not-acknowledged";
                assignment.SanitizedFailure =
                    $"Office {nodeDescription} did not acknowledge signed assignment {assignment.Id:D} " +
                    $"after {assignment.Attempt} delivery attempts. Check the CSweet.Office.Node log for " +
                    "a control-session or assignment-envelope error.";
                continue;
            }
            assignment.ExecutionNodeId = null;
            assignment.Status = ExecutionAssignmentStatus.Pending;
            assignment.FencingEpoch++;
            assignment.Attempt++;
            assignment.LeaseExpiresAt = null;
            assignment.AssignedAt = null;
            assignment.StartedAt = null;
            assignment.FailureCode = "assignment-lease-expired";
            assignment.SanitizedFailure = "The execution node did not renew its assignment lease; the prior epoch was fenced.";
        }
        if (expired.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    private async Task<NodeSelection?> SelectNodeAsync(
        ExecutionWorkloadAssignment assignment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var staleAt = now.Subtract(HeartbeatFreshness);
        var pool = await dbContext.ExecutionPools.AsNoTracking()
            .SingleAsync(x => x.Id == assignment.ExecutionPoolId, cancellationToken);
        var activeInPool = await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .CountAsync(x => x.ExecutionPoolId == assignment.ExecutionPoolId && ActiveStatuses.Contains(x.Status),
                cancellationToken);
        if (activeInPool >= pool.MaximumActiveWorkloads || !PoolAllows(pool, assignment.BusinessId)) return null;
        var nodes = await dbContext.ExecutionNodes
            .Include(x => x.ExecutionPool)
            .Include(x => x.Providers)
            .Where(x => x.ExecutionPoolId == assignment.ExecutionPoolId &&
                x.ExecutionPool!.IsEnabled && x.Status == ExecutionNodeStatus.Ready &&
                x.ApprovedAt != null && x.DrainingAt == null && x.RevokedAt == null &&
                x.LastHeartbeatAt >= staleAt && x.CertificateExpiresAt > now &&
                x.AllocatableCpuCount >= assignment.ReservedCpuCount &&
                x.AllocatableMemoryMb >= assignment.ReservedMemoryMb &&
                x.AllocatableDiskMb >= assignment.ReservedDiskMb)
            .ToListAsync(cancellationToken);
        nodes = nodes.Where(node =>
                string.Equals(node.ProtocolVersion, "1.0", StringComparison.Ordinal) &&
                Version.TryParse(node.NodeVersion, out var version) &&
                version >= ExecutionFleetService.MinimumNodeVersion &&
                LabelsMatch(pool.RequiredLabelsJson, node.LabelsJson) &&
                SecurityPostureAllows(node.LabelsJson, assignment.SpecificationJson))
            .ToList();
        if (nodes.Count == 0) return null;
        var nodeIds = nodes.Select(x => x.Id).ToArray();
        var reservations = await dbContext.ExecutionWorkloadAssignments.AsNoTracking()
            .Where(x => x.ExecutionNodeId != null && nodeIds.Contains(x.ExecutionNodeId.Value) && ActiveStatuses.Contains(x.Status))
            .GroupBy(x => x.ExecutionNodeId!.Value)
            .Select(group => new NodeReservation(
                group.Key,
                group.Sum(x => x.ReservedCpuCount),
                group.Sum(x => x.ReservedMemoryMb),
                group.Sum(x => x.ReservedDiskMb),
                group.Count()))
            .ToDictionaryAsync(x => x.NodeId, cancellationToken);

        return nodes.SelectMany(node => node.Providers
                .Where(provider => provider.IsAvailable &&
                    string.Equals(provider.BrokerProtocolVersion, "1.0", StringComparison.Ordinal) &&
                    IsSha256(provider.CertificationEvidenceDigest) &&
                    provider.CertifiedAt <= now &&
                    (provider.CertificationExpiresAt == null || provider.CertificationExpiresAt > now) &&
                    string.Equals(provider.GuestImageDigest, assignment.GuestImageDigest, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(assignment.ProviderId) || string.Equals(provider.ProviderId, assignment.ProviderId, StringComparison.Ordinal)) &&
                    (assignment.WorkloadKind == ExecutionWorkloadKind.Builder
                        ? provider.SupportsBuilderWorkloads : provider.SupportsRuntimeWorkloads))
                .Select(provider => Score(node, provider, reservations.GetValueOrDefault(node.Id), assignment)))
            .Where(selection => selection.Fits)
            .OrderBy(selection => selection.DominantUtilization)
            .ThenBy(selection => selection.Node.LastAssignedAt ?? DateTimeOffset.MinValue)
            .ThenBy(selection => selection.Node.Id)
            .FirstOrDefault();
    }

    private static NodeSelection Score(
        ExecutionNode node,
        ExecutionNodeProvider provider,
        NodeReservation? reservation,
        ExecutionWorkloadAssignment assignment)
    {
        var cpu = reservation?.Cpu ?? 0;
        var memory = reservation?.Memory ?? 0;
        var disk = reservation?.Disk ?? 0;
        var count = reservation?.Count ?? 0;
        var score = ExecutionPlacementPolicy.Score(new ExecutionPlacementResources(
            node.AllocatableCpuCount, node.AllocatableMemoryMb, node.AllocatableDiskMb,
            node.MaximumConcurrentWorkloads, cpu, memory, disk, count,
            assignment.ReservedCpuCount, assignment.ReservedMemoryMb, assignment.ReservedDiskMb));
        return new NodeSelection(node, provider, score.Fits, score.DominantUtilization);
    }

    private async Task<Guid> DefaultPoolIdAsync(ExecutionWorkloadKind kind, CancellationToken cancellationToken)
    {
        var settings = await dbContext.AgentRuntimeGlobalSettings.AsNoTracking()
            .OrderBy(x => x.UpdatedAt).FirstAsync(cancellationToken);
        return kind == ExecutionWorkloadKind.Builder
            ? settings.DefaultBuildExecutionPoolId ?? throw new InvalidOperationException("The default build execution pool is not configured.")
            : settings.DefaultRuntimeExecutionPoolId ?? throw new InvalidOperationException("The default runtime execution pool is not configured.");
    }

    private static void Validate(ExecutionWorkloadRequest request)
    {
        if ((request.AgentBuildJobId.HasValue ? 1 : 0) + (request.AgentRuntimeInstanceId.HasValue ? 1 : 0) != 1 ||
            request.WorkloadKind == ExecutionWorkloadKind.Builder != request.AgentBuildJobId.HasValue ||
            !IsSha256(request.GuestImageDigest) ||
            request.ArtifactDigest is not null && !IsSha256(request.ArtifactDigest) ||
            request.CpuCount < 1 || request.MemoryMb < 128 || request.DiskMb < 64 ||
            request.SpecificationJson.Length is < 2 or > 1024 * 1024)
            throw new ArgumentException("The execution workload request is invalid.", nameof(request));
        using var _ = JsonDocument.Parse(request.SpecificationJson);
    }

    private static bool CanTransition(ExecutionAssignmentStatus current, ExecutionAssignmentStatus next) =>
        (current, next) switch
        {
            (ExecutionAssignmentStatus.Assigned, ExecutionAssignmentStatus.Starting or ExecutionAssignmentStatus.Cancelled or ExecutionAssignmentStatus.Failed) => true,
            (ExecutionAssignmentStatus.Starting, ExecutionAssignmentStatus.Running or ExecutionAssignmentStatus.Cancelled or ExecutionAssignmentStatus.Failed) => true,
            (ExecutionAssignmentStatus.Running, ExecutionAssignmentStatus.Stopping or ExecutionAssignmentStatus.Completed or ExecutionAssignmentStatus.Cancelled or ExecutionAssignmentStatus.Failed) => true,
            (ExecutionAssignmentStatus.Stopping, ExecutionAssignmentStatus.Completed or ExecutionAssignmentStatus.Cancelled or ExecutionAssignmentStatus.Failed) => true,
            _ => false
        };

    private static string HashToken() => Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string? Bound(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : new string(value.Where(character => !char.IsControl(character)).Take(maximum).ToArray());

    private static bool PoolAllows(ExecutionPool pool, string? businessId)
    {
        try
        {
            var allowed = JsonSerializer.Deserialize<string[]>(pool.AllowedBusinessIdsJson) ?? [];
            return allowed.Length == 0 || businessId is not null &&
                allowed.Contains(businessId, StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static bool LabelsMatch(string requiredJson, string labelsJson)
    {
        try
        {
            var required = JsonSerializer.Deserialize<Dictionary<string, string>>(requiredJson) ?? [];
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelsJson) ?? [];
            return required.All(item => labels.TryGetValue(item.Key, out var value) &&
                string.Equals(value, item.Value, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }

    private static bool SecurityPostureAllows(string labelsJson, string specificationJson)
    {
        try
        {
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelsJson) ?? [];
            var profile = labels.GetValueOrDefault("csweet.security.profile") ?? "baseline";
            if (!string.Equals(profile, "development", StringComparison.Ordinal)) return true;
            if (!string.Equals(labels.GetValueOrDefault("csweet.security.development-assignments"), "true", StringComparison.Ordinal))
                return false;
            using var specification = JsonDocument.Parse(specificationJson);
            return specification.RootElement.TryGetProperty("allowDevelopmentSecurityPosture", out var allowed) &&
                allowed.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string BindSecurityPolicy(string specificationJson, bool allowDevelopmentSecurityPosture)
    {
        using var document = JsonDocument.Parse(specificationJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The workload specification must be a JSON object.", nameof(specificationJson));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "allowDevelopmentSecurityPosture", StringComparison.Ordinal)) continue;
                property.WriteTo(writer);
            }
            writer.WriteBoolean("allowDevelopmentSecurityPosture", allowDevelopmentSecurityPosture);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    private static bool IsSha256(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private sealed record NodeSelection(
        ExecutionNode Node,
        ExecutionNodeProvider Provider,
        bool Fits,
        double DominantUtilization);

    private sealed record NodeReservation(Guid NodeId, int Cpu, int Memory, int Disk, int Count);
}
