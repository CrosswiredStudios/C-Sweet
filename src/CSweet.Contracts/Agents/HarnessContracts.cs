namespace CSweet.Contracts.Agents;

public sealed record HarnessRoute(string Stage, Guid ProviderId, string Model,
    decimal? InputPricePerMillion = null, decimal? OutputPricePerMillion = null,
    IReadOnlyList<HarnessRouteTarget>? Fallbacks = null);
/// <summary>An approved alternate target, tried in order only when the previous target was unavailable before any output.</summary>
public sealed record HarnessRouteTarget(Guid ProviderId, string Model,
    decimal? InputPricePerMillion = null, decimal? OutputPricePerMillion = null);
/// <summary>The agent's own effective harness state. Read-only; it grants nothing.</summary>
public sealed record HarnessStatus(long PolicyRevision, string Stage, string? Provider, string? Model, int FallbackCount,
    bool CompactContext, bool ContextRetrievalAvailable, long? TaskTokenLimit, long? TaskTokensRemaining,
    decimal? TaskCostLimit, decimal? TaskCostRemaining, IReadOnlyList<string> Skills);
public sealed record HarnessPolicy(long Revision = 0, bool CompactContext = false,
    long? TaskTokenLimit = null, decimal? TaskCostLimit = null,
    IReadOnlyList<HarnessRoute>? Routes = null, IReadOnlyList<Guid>? SkillRevisionIds = null, int JudgmentOutputTokens = 512);
public sealed record SaveHarnessPolicy(long ExpectedRevision, HarnessPolicy Policy);
public sealed record PublishHarnessSkill(string Name, string Instructions, IReadOnlyList<string> RequiredCapabilities);
public sealed record HarnessSkillRevision(Guid Id, string Name, int Version, string Digest,
    string Instructions, IReadOnlyList<string> RequiredCapabilities, bool Enabled, DateTimeOffset CreatedAt);
public sealed record DoctorFinding(string Code, string State, string Title, string Detail, string RepairUrl);
public sealed record HarnessWorkspace(HarnessPolicy Policy, IReadOnlyList<HarnessSkillRevision> Skills,
    IReadOnlyList<DoctorFinding> Findings, DateTimeOffset CheckedAt, IReadOnlyList<HarnessProviderOption> Providers);
public sealed record HarnessProviderOption(Guid Id, string Name, string DefaultModel);
public sealed record HarnessJudgmentRequest(string Kind, string Input);
public sealed record HarnessJudgmentResult(bool Valid, string? Choice, string? Error, bool Advisory = true);
