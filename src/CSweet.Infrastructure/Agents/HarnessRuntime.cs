using System.Text;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CSweet.Infrastructure.Agents;

public sealed record HarnessPreparation(HarnessPolicy Policy, string Stage, HarnessRoute? Route,
    string SkillInstructions, IReadOnlyList<BusinessSkillRevision> Skills)
{
    /// <summary>Zero for the primary target; otherwise the fallback position being attempted.</summary>
    public int RouteAttempt { get; init; }
    public bool HasNextTarget => Route?.Fallbacks is { } fallbacks && RouteAttempt < fallbacks.Count;

    /// <summary>The same preparation aimed at the next approved fallback target, priced by that target.</summary>
    public HarnessPreparation? Next()
    {
        if (Route is not { Fallbacks: { } fallbacks } primary || RouteAttempt >= fallbacks.Count) return null;
        var target = fallbacks[RouteAttempt];
        return this with { RouteAttempt = RouteAttempt + 1, Route = primary with { ProviderId = target.ProviderId, Model = target.Model,
            InputPricePerMillion = target.InputPricePerMillion, OutputPricePerMillion = target.OutputPricePerMillion } };
    }
}

/// <summary>The shared task ceiling cannot cover the next provider attempt. Not retryable without human review.</summary>
public sealed class HarnessBudgetExceededException(string message) : InvalidOperationException(message);

/// <summary>The approved harness policy or skill set changed while a request waited. Retrying re-reads current policy.</summary>
public sealed class HarnessPolicyChangedException(string message) : InvalidOperationException(message);

public static class HarnessRuntime
{
    /// <summary>Stage comes from the platform's durable dispatch, never request telemetry.</summary>
    public static async Task<string> ResolveStageAsync(CSweetDbContext db, Guid installation, Guid? work, CancellationToken token, string? trustedStage = null)
    {
        if (trustedStage is not null) return trustedStage;
        if (work is not { } workId) return "Default";
        var keys = await db.WorkExecutionAttempts.AsNoTracking().Where(x => x.AgentWorkItemId == workId && x.StageExecution!.AgentInstallationId == installation)
            .Select(x => x.StageExecution!.StageKey).Distinct().Take(2).ToListAsync(token);
        return keys.Count == 1 ? keys[0] : "Default";
    }

    public static HarnessRoute? SelectRoute(HarnessPolicy policy, string stage) =>
        (policy.Routes ?? []).SingleOrDefault(x => x.Stage == stage) ?? (policy.Routes ?? []).SingleOrDefault(x => x.Stage == "Default");

    /// <summary>True when the policy approves this exact provider/model as a primary or fallback target.</summary>
    public static bool Approves(HarnessPolicy policy, Guid provider, string model) => (policy.Routes ?? []).Any(route =>
        route.ProviderId == provider && route.Model == model || (route.Fallbacks ?? []).Any(x => x.ProviderId == provider && x.Model == model));

    /// <summary>The provider whose concurrency slot a request will occupy first.</summary>
    public static async Task<Guid> ResolvePrimaryProviderAsync(CSweetDbContext db, Guid installation, Guid? work, Guid requested, CancellationToken token)
    {
        var policy = await HarnessWorkspaceService.ReadPolicyAsync(db, installation, token);
        return SelectRoute(policy, await ResolveStageAsync(db, installation, work, token))?.ProviderId ?? requested;
    }

    public static async Task<HarnessPreparation> PrepareAsync(CSweetDbContext db, Guid installation, Guid? work, CancellationToken token, string? trustedStage = null)
    {
        var policy = await HarnessWorkspaceService.ReadPolicyAsync(db, installation, token);
        var stage = await ResolveStageAsync(db, installation, work, token, trustedStage);
        var route = SelectRoute(policy, stage);
        if ((policy.SkillRevisionIds?.Count ?? 0) == 0) return new(policy, stage, route, "", []);
        var business = await db.AgentInstallations.Where(x => x.Id == installation).Select(x => x.BusinessId).SingleAsync(token);
        var ids = policy.SkillRevisionIds ?? [];
        var skills = await db.BusinessSkillRevisions.AsNoTracking().Where(x => ids.Contains(x.Id) && x.BusinessId == business && x.Enabled).ToListAsync(token);
        // A revoked skill stops the request instead of silently changing its instructions.
        if (skills.Count != ids.Count) throw new InvalidOperationException("An assigned skill was disabled or removed. Review the harness policy.");
        var grant = await db.AgentInstallationGrants.AsNoTracking().Where(x => x.AgentInstallationId == installation).Select(x => x.RequiredCapabilitiesJson).SingleOrDefaultAsync(token);
        var granted = JsonSerializer.Deserialize<string[]>(grant ?? "[]") ?? [];
        foreach (var skill in skills)
            if ((JsonSerializer.Deserialize<string[]>(skill.RequiredCapabilitiesJson) ?? []).Except(granted, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("An assigned skill requires a capability which is no longer approved.");
        var ordered = ids.Select(id => skills.Single(x => x.Id == id)).ToArray();
        return new(policy, stage, route, string.Join("\n\n", ordered.Select(x => $"Business skill {x.Name} v{x.Version} ({x.Digest}):\n{x.Instructions}")), ordered);
    }

    public static async Task ValidateAsync(CSweetDbContext db, Guid installation, HarnessPreparation expected, CancellationToken token)
    {
        var current = await HarnessWorkspaceService.ReadPolicyAsync(db, installation, token);
        if (current.Revision != expected.Policy.Revision) throw new HarnessPolicyChangedException("The harness policy changed before dispatch. Retry with current policy.");
        var business = await db.AgentInstallations.Where(x => x.Id == installation).Select(x => x.BusinessId).SingleAsync(token);
        var ids = expected.Skills.Select(x => x.Id).ToArray();
        var skills = await db.BusinessSkillRevisions.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.BusinessId == business && x.Enabled).ToListAsync(token);
        if (skills.Count != ids.Length)
            throw new HarnessPolicyChangedException("An assigned skill was disabled before dispatch.");
        // Re-check grants at dispatch: a capability revoked while the request waited must stop it here.
        var grant = await db.AgentInstallationGrants.AsNoTracking().Where(x => x.AgentInstallationId == installation)
            .Select(x => x.RequiredCapabilitiesJson).SingleOrDefaultAsync(token);
        var granted = JsonSerializer.Deserialize<string[]>(grant ?? "[]") ?? [];
        foreach (var skill in skills)
            if ((JsonSerializer.Deserialize<string[]>(skill.RequiredCapabilitiesJson) ?? []).Except(granted, StringComparer.Ordinal).Any())
                throw new HarnessPolicyChangedException("An assigned skill requires a capability which is no longer approved.");
    }

    /// <summary>The caller's own effective route and remaining shared budget. Never exposes other installations' policy.</summary>
    public static async Task<HarnessStatus> ReadStatusAsync(CSweetDbContext db, string business, Guid installation, Guid? work,
        bool contextRetrievalGranted, CancellationToken token, HarnessDecisionEngine? decisionEngine = null)
    {
        var policy = await HarnessWorkspaceService.ReadPolicyAsync(db, installation, token);
        var stage = await ResolveStageAsync(db, installation, work, token);
        var route = SelectRoute(policy, stage);
        var provider = route is null ? null : await db.LlmProviderProfiles.AsNoTracking().Where(x => x.Id == route.ProviderId)
            .Select(x => x.Name).SingleOrDefaultAsync(token);
        var ids = policy.SkillRevisionIds ?? [];
        var skills = await db.BusinessSkillRevisions.AsNoTracking().Where(x => ids.Contains(x.Id) && x.BusinessId == business && x.Enabled)
            .OrderBy(x => x.Name).Select(x => x.Name + " v" + x.Version).ToArrayAsync(token);
        // The shared account is keyed by the canonical root task; the latest run on this work carries that lineage.
        var run = work is { } workId ? await db.AgentRunLogs.AsNoTracking().Where(x => x.AgentInstallationId == installation && x.AgentWorkItemId == workId)
            .OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(token) : null;
        var key = run is null ? work is { } id ? $"{business}:work:{id:D}" : $"{business}:installation:{installation:D}" : BudgetKey(run, business, installation);
        var budget = await db.HarnessTaskBudgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == key, token);
        var tokenLimit = budget?.TokenLimit ?? policy.TaskTokenLimit;
        var costLimit = budget?.CostLimit ?? policy.TaskCostLimit;
        return new(policy.Revision, stage, provider, route?.Model, route?.Fallbacks?.Count ?? 0, policy.CompactContext,
            policy.CompactContext && contextRetrievalGranted,
            tokenLimit, tokenLimit is { } tl ? Math.Max(0, tl - (budget?.ChargedTokens ?? 0)) : null,
            costLimit, costLimit is { } cl ? Math.Max(0, cl - (budget?.ChargedCost ?? 0)) : null, skills,
            policy.CompactContext && policy.DecisionCompaction && decisionEngine?.Available == true,
            decisionEngine?.Available == true ? decisionEngine.Host : null, decisionEngine?.Available == true ? decisionEngine.Model : null);
    }

    public static string BudgetKey(AgentRunLog run, string business, Guid installation)
    {
        var ancestry = JsonSerializer.Deserialize<Guid[]>(run.AncestorWorkItemIdsJson ?? "[]") ?? [];
        return run.WorkItemId is { } task ? $"{business}:task:{(ancestry.LastOrDefault() is var root && root != Guid.Empty ? root : task):D}"
            : run.AgentWorkItemId is { } work ? $"{business}:work:{work:D}" : $"{business}:installation:{installation:D}";
    }
}

/// <summary>Wire-attempt reservations are durable. Unknown/abandoned usage remains charged, never reclaimed optimistically.</summary>
public sealed class HarnessBudgetSession(CSweetDbContext source, AgentRunLog run, HarnessPreparation preparation,
    string business, Guid installation, long inputUpperBound, int outputLimit) : IAsyncDisposable
{
    private readonly List<Guid> reservations = [];
    private CSweetDbContext Open() => new((DbContextOptions<CSweetDbContext>)source.GetService<IDbContextOptions>());

    private static decimal RoundCost(decimal amount) => Math.Ceiling(amount * 10_000_000_000m) / 10_000_000_000m;

    public async Task ReserveAsync(CancellationToken token)
    {
        var key = HarnessRuntime.BudgetKey(run, business, installation);
        await using var db = Open();
        await using var transaction = await GovernanceTransaction.OpenAsync(db, $"budget:{key}", token);
        var row = await db.HarnessTaskBudgets.SingleOrDefaultAsync(x => x.Id == key, token);
        var policy = preparation.Policy;
        if (run.WorkItemId is not null && Guid.TryParse(business, out var organization))
        {
            var ancestors = JsonSerializer.Deserialize<Guid[]>(run.AncestorWorkItemIdsJson ?? "[]") ?? [];
            var root = ancestors.LastOrDefault();
            if (root == Guid.Empty) root = run.WorkItemId.Value;
            var owner = await db.CoreWorkTasks.AsNoTracking().Where(x => x.Id == root && x.OrganizationId == organization)
                .Select(x => x.AssignedAgentInstallationId).SingleOrDefaultAsync(token);
            if (owner is { } ownerInstallation && ownerInstallation != installation)
            {
                var rootPolicy = await HarnessWorkspaceService.ReadPolicyAsync(db, ownerInstallation, token);
                policy = policy with {
                    TaskTokenLimit = rootPolicy.TaskTokenLimit is { } rt ? Math.Min(rt, policy.TaskTokenLimit ?? long.MaxValue) : policy.TaskTokenLimit,
                    TaskCostLimit = rootPolicy.TaskCostLimit is { } rc ? Math.Min(rc, policy.TaskCostLimit ?? decimal.MaxValue) : policy.TaskCostLimit
                };
            }
        }
        if (row is null && policy.TaskTokenLimit is null && policy.TaskCostLimit is null) return;
        row ??= new() { Id = key, TokenLimit = policy.TaskTokenLimit, CostLimit = policy.TaskCostLimit };
        if (db.Entry(row).State == EntityState.Detached) db.HarnessTaskBudgets.Add(row);
        // A descendant may tighten, but cannot raise a task ceiling established by its owner.
        if (policy.TaskTokenLimit is { } tl) row.TokenLimit = row.TokenLimit is { } old ? Math.Min(tl, old) : tl;
        if (policy.TaskCostLimit is { } cl) row.CostLimit = row.CostLimit is { } old ? Math.Min(cl, old) : cl;
        var route = preparation.Route;
        if (row.CostLimit.HasValue && row.HasUnpricedUsage) throw new HarnessBudgetExceededException("This task has prior unpriced usage. Its monetary ceiling cannot be verified.");
        if (row.CostLimit.HasValue && (route?.InputPricePerMillion is null || route.OutputPricePerMillion is null))
            throw new HarnessBudgetExceededException("The task has a monetary ceiling but this route has no approved pricing.");
        var tokens = checked(inputUpperBound + outputLimit);
        var cost = RoundCost((inputUpperBound * (route?.InputPricePerMillion ?? 0) + outputLimit * (route?.OutputPricePerMillion ?? 0)) / 1_000_000m);
        if (row.TokenLimit is { } tokenLimit && tokens > tokenLimit - row.ChargedTokens || row.CostLimit is { } costLimit && cost > costLimit - row.ChargedCost)
            throw new HarnessBudgetExceededException(
                $"The shared task budget cannot reserve this provider attempt (needs {tokens} tokens" +
                (row.TokenLimit is { } t ? $", {Math.Max(0, t - row.ChargedTokens)} remaining" : "") +
                (row.CostLimit is { } c ? $"; needs ${cost}, ${Math.Max(0, c - row.ChargedCost)} remaining" : "") +
                "). Shorten the context, lower the output budget, or ask a manager to raise the task ceiling.");
        var reservation = new HarnessBudgetReservation { Id = Guid.NewGuid(), BudgetId = key, RunId = run.Id,
            Tokens = tokens, Cost = cost, CreatedAt = DateTimeOffset.UtcNow };
        row.HasUnpricedUsage |= route?.InputPricePerMillion is null || route.OutputPricePerMillion is null;
        row.ChargedTokens += tokens; row.ChargedCost += cost;
        db.HarnessBudgetReservations.Add(reservation);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); reservations.Add(reservation.Id);
    }

    public async ValueTask DisposeAsync()
    {
        if (reservations.Count == 0 || run.Status != "Completed" || run.TokenInputCount is null || run.TokenOutputCount is null) return;
        // Only the successful final attempt has known usage. Failed transport retries retain their whole reservation.
        await using var db = Open();
        var key = HarnessRuntime.BudgetKey(run, business, installation);
        await using var transaction = await GovernanceTransaction.OpenAsync(db, $"budget:{key}", CancellationToken.None);
        var id = reservations[^1];
        var reservation = await db.HarnessBudgetReservations.SingleAsync(x => x.Id == id);
        if (reservation.Settled) return;
        var row = await db.HarnessTaskBudgets.SingleAsync(x => x.Id == key);
        var tokens = (long)run.TokenInputCount.Value + run.TokenOutputCount.Value;
        var route = preparation.Route;
        var cost = RoundCost((run.TokenInputCount.Value * (route?.InputPricePerMillion ?? 0) + run.TokenOutputCount.Value * (route?.OutputPricePerMillion ?? 0)) / 1_000_000m);
        row.ChargedTokens += tokens - reservation.Tokens; row.ChargedCost += cost - reservation.Cost;
        reservation.Settled = true; reservation.Tokens = tokens; reservation.Cost = cost;
        await db.SaveChangesAsync(); await transaction.CommitAsync(CancellationToken.None);
    }
}
