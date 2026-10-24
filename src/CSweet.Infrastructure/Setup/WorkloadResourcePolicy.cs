using CSweet.Domain.Setup;

namespace CSweet.Infrastructure.Setup;

/// <summary>Trusted, discrete sizing. Model estimates cannot change these bounds.</summary>
public static class WorkloadResourcePolicy
{
    public static readonly IReadOnlyList<int> Tiers = Array.AsReadOnly(new[] { 2048, 4096, 8192, 16384 });
    public static int Processes(AgentRuntimeGlobalSettings settings, bool codingRuntime) =>
        codingRuntime ? settings.CodingWorkloadProcessLimit : settings.DefaultWorkloadProcessLimit;
    public static int Initial(AgentRuntimeGlobalSettings settings, int grantMemoryMb) =>
        Tier(Math.Max(settings.CodingInitialMemoryMb, grantMemoryMb), settings.CodingMaximumMemoryMb);

    public static int Tier(int requested, int maximum) => Tiers.FirstOrDefault(x => x >= requested && x <= maximum) is > 0 and var tier
        ? tier : throw new InvalidOperationException("runtime.resources.policy_limit: requested coding memory exceeds the approved policy.");

    public static int Grow(int current, int requested, int maximum, int replacements, int maximumReplacements, bool confirmedMemoryFailure = false)
    {
        if (replacements >= maximumReplacements || requested <= current)
            throw new InvalidOperationException("runtime.resources.exhausted: no further automatic replacement is authorized.");
        // A proactive estimate can select its approved tier directly, avoiding unnecessary
        // VM boots and model restarts. A confirmed failure retries only at the next tier.
        if (!confirmedMemoryFailure) return Tier(requested, maximum);
        var next = Tiers.FirstOrDefault(x => x > current && x <= maximum);
        return next > 0 ? next : throw new InvalidOperationException("runtime.resources.policy_limit: no larger approved tier is available.");
    }
}
