using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionPoolAdministrationService(
    CSweetDbContext db,
    IAuditEventWriter audit,
    TimeProvider clock) : IExecutionPoolAdministrationService
{
    private static readonly ExecutionAssignmentStatus[] ActiveStatuses =
    [
        ExecutionAssignmentStatus.Pending,
        ExecutionAssignmentStatus.Assigned,
        ExecutionAssignmentStatus.Starting,
        ExecutionAssignmentStatus.Running,
        ExecutionAssignmentStatus.Stopping
    ];

    public async Task<ExecutionFleetMutationResponse> CreatePoolAsync(
        CreateExecutionPoolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(request.Name, request.MaximumActiveWorkloads, request.RequiredLabels,
                request.AllowedBusinessIds, out var policy, out var error))
            return Failure("invalid_pool_policy", error);
        var normalizedName = policy.Name.ToUpperInvariant();
        if (await db.ExecutionPools.AnyAsync(x => x.Name.ToUpper() == normalizedName, cancellationToken))
            return Failure("pool_name_exists", "An execution pool with that name already exists.");
        var now = clock.GetUtcNow();
        var pool = new ExecutionPool
        {
            Id = Guid.NewGuid(),
            Name = policy.Name,
            IsEnabled = true,
            MaximumActiveWorkloads = policy.MaximumActiveWorkloads,
            RequiredLabelsJson = JsonSerializer.Serialize(policy.RequiredLabels),
            AllowedBusinessIdsJson = JsonSerializer.Serialize(policy.AllowedBusinessIds),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExecutionPools.Add(pool);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("execution-pool.created", nameof(ExecutionPool), pool.Id,
            $"Created execution pool {pool.Name}.", cancellationToken: cancellationToken);
        return Success($"Execution pool {pool.Name} was created.");
    }

    public async Task<ExecutionFleetMutationResponse> UpdatePoolAsync(
        Guid poolId,
        UpdateExecutionPoolRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(request.Name, request.MaximumActiveWorkloads, request.RequiredLabels,
                request.AllowedBusinessIds, out var policy, out var error))
            return Failure("invalid_pool_policy", error);
        var pool = await db.ExecutionPools.SingleOrDefaultAsync(x => x.Id == poolId, cancellationToken);
        if (pool is null) return Failure("pool_not_found", "The execution pool was not found.");
        var normalizedName = policy.Name.ToUpperInvariant();
        if (await db.ExecutionPools.AnyAsync(
                x => x.Id != poolId && x.Name.ToUpper() == normalizedName, cancellationToken))
            return Failure("pool_name_exists", "An execution pool with that name already exists.");
        if (!request.IsEnabled && (pool.IsDefaultBuildPool || pool.IsDefaultRuntimePool ||
            request.SetAsDefaultBuildPool || request.SetAsDefaultRuntimePool))
            return Failure("default_pool_required", "A default build or runtime pool cannot be disabled.");
        if (!request.IsEnabled && await db.ExecutionWorkloadAssignments.AnyAsync(
                x => x.ExecutionPoolId == poolId && ActiveStatuses.Contains(x.Status), cancellationToken))
            return Failure("pool_has_active_work", "Drain or complete the pool's active assignments before disabling it.");

        await using IDbContextTransaction? transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var settings = await db.AgentRuntimeGlobalSettings.OrderBy(x => x.UpdatedAt)
            .FirstAsync(cancellationToken);
        var clearedDefault = false;
        if (request.SetAsDefaultBuildPool)
        {
            foreach (var current in await db.ExecutionPools.Where(x => x.IsDefaultBuildPool && x.Id != poolId)
                         .ToListAsync(cancellationToken))
            {
                current.IsDefaultBuildPool = false;
                clearedDefault = true;
            }
        }
        if (request.SetAsDefaultRuntimePool)
        {
            foreach (var current in await db.ExecutionPools.Where(x => x.IsDefaultRuntimePool && x.Id != poolId)
                         .ToListAsync(cancellationToken))
            {
                current.IsDefaultRuntimePool = false;
                clearedDefault = true;
            }
        }
        if (clearedDefault)
            await db.SaveChangesAsync(cancellationToken);
        if (request.SetAsDefaultBuildPool)
        {
            pool.IsDefaultBuildPool = true;
            settings.DefaultBuildExecutionPoolId = pool.Id;
        }
        if (request.SetAsDefaultRuntimePool)
        {
            pool.IsDefaultRuntimePool = true;
            settings.DefaultRuntimeExecutionPoolId = pool.Id;
        }
        pool.Name = policy.Name;
        pool.IsEnabled = request.IsEnabled;
        pool.MaximumActiveWorkloads = policy.MaximumActiveWorkloads;
        pool.RequiredLabelsJson = JsonSerializer.Serialize(policy.RequiredLabels);
        pool.AllowedBusinessIdsJson = JsonSerializer.Serialize(policy.AllowedBusinessIds);
        pool.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        await audit.WriteAsync("execution-pool.updated", nameof(ExecutionPool), pool.Id,
            $"Updated execution pool {pool.Name}.", cancellationToken: cancellationToken);
        return Success($"Execution pool {pool.Name} was updated.");
    }

    public async Task<ExecutionFleetMutationResponse> DeletePoolAsync(
        Guid poolId,
        CancellationToken cancellationToken = default)
    {
        var pool = await db.ExecutionPools.SingleOrDefaultAsync(x => x.Id == poolId, cancellationToken);
        if (pool is null) return Failure("pool_not_found", "The execution pool was not found.");
        if (pool.IsDefaultBuildPool || pool.IsDefaultRuntimePool)
            return Failure("default_pool_required", "Default build and runtime pools cannot be deleted.");
        var inUse = await db.ExecutionNodes.AnyAsync(x => x.ExecutionPoolId == poolId, cancellationToken) ||
            await db.ExecutionNodeEnrollments.AnyAsync(x => x.ExecutionPoolId == poolId, cancellationToken) ||
            await db.ExecutionWorkloadAssignments.AnyAsync(x => x.ExecutionPoolId == poolId, cancellationToken) ||
            await db.AgentBuildJobs.AnyAsync(x => x.ExecutionPoolId == poolId, cancellationToken) ||
            await db.AgentInstallations.AnyAsync(x => x.ExecutionPoolId == poolId, cancellationToken);
        if (inUse)
            return Failure("pool_in_use", "The execution pool is referenced by nodes, enrollments, workloads, builds, or installations.");
        db.ExecutionPools.Remove(pool);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("execution-pool.deleted", nameof(ExecutionPool), pool.Id,
            $"Deleted execution pool {pool.Name}.", cancellationToken: cancellationToken);
        return Success($"Execution pool {pool.Name} was deleted.");
    }

    public async Task<ExecutionFleetMutationResponse> SetInstallationPoolAsync(
        Guid installationId,
        UpdateAgentExecutionPoolRequest request,
        CancellationToken cancellationToken = default)
    {
        var installation = await db.AgentInstallations.SingleOrDefaultAsync(
            x => x.Id == installationId && x.RevisionStatus == PluginRevisionStatus.Active,
            cancellationToken);
        if (installation is null)
            return Failure("installation_not_found", "The active agent installation was not found.");
        if (request.ExecutionPoolId is { } poolId)
        {
            var pool = await db.ExecutionPools.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == poolId && x.IsEnabled, cancellationToken);
            if (pool is null)
                return Failure("pool_unavailable", "The selected execution pool is missing or disabled.");
            if (!AllowsBusiness(pool.AllowedBusinessIdsJson, installation.BusinessId))
                return Failure("business_not_allowed", "The selected pool does not allow this installation's business.");
        }
        installation.ExecutionPoolId = request.ExecutionPoolId;
        installation.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync("agent-installation.execution-pool.updated", nameof(AgentInstallation), installation.Id,
            request.ExecutionPoolId is null
                ? "Cleared the installation execution-pool override."
                : $"Set execution-pool override to {request.ExecutionPoolId:D}.",
            cancellationToken: cancellationToken);
        return Success(request.ExecutionPoolId is null
            ? "The installation now uses the default runtime pool."
            : "The installation execution-pool override was updated.");
    }

    private static bool TryNormalize(
        string name,
        int maximumActiveWorkloads,
        IReadOnlyDictionary<string, string>? requiredLabels,
        IReadOnlyList<string>? allowedBusinessIds,
        out PoolPolicy policy,
        out string error)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        var labels = requiredLabels ?? new Dictionary<string, string>();
        var businesses = (allowedBusinessIds ?? []).Select(x => x?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalizedName.Length is < 1 or > 160 || normalizedName.Any(char.IsControl))
        {
            policy = default!;
            error = "Pool names must contain 1 to 160 printable characters.";
            return false;
        }
        if (maximumActiveWorkloads is < 1 or > 100_000)
        {
            policy = default!;
            error = "The active workload limit must be between 1 and 100,000.";
            return false;
        }
        if (!ValidLabels(labels))
        {
            policy = default!;
            error = "Required labels contain an invalid key or value.";
            return false;
        }
        if (businesses.Length > 256 || businesses.Any(x => x.Length is < 1 or > 128 ||
                x.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))))
        {
            policy = default!;
            error = "Business allowlists may contain at most 256 valid identifiers.";
            return false;
        }
        policy = new PoolPolicy(normalizedName, maximumActiveWorkloads,
            labels.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value), businesses);
        error = string.Empty;
        return true;
    }

    private static bool ValidLabels(IReadOnlyDictionary<string, string> labels) => labels.Count <= 64 &&
        labels.All(x => x.Key.Length is >= 1 and <= 64 && x.Value.Length <= 256 &&
            x.Key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') &&
            !x.Value.Any(char.IsControl));

    private static bool AllowsBusiness(string json, string businessId)
    {
        try
        {
            var allowed = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return allowed.Length == 0 || allowed.Contains(businessId, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ExecutionFleetMutationResponse Success(string message) => new(true, null, message);
    private static ExecutionFleetMutationResponse Failure(string code, string message) => new(false, code, message);

    private sealed record PoolPolicy(
        string Name,
        int MaximumActiveWorkloads,
        IReadOnlyDictionary<string, string> RequiredLabels,
        IReadOnlyList<string> AllowedBusinessIds);
}
