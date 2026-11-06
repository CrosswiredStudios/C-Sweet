using CSweet.Contracts.Agents;

namespace CSweet.Application.Security;

/// <summary>
/// Vocabulary shared by the temporal policy service, the endpoints and tests. Everything that
/// decides "how often" lives here so a record and its count can never disagree about the key.
/// </summary>
public static class AgentPolicyRules
{
    /// <summary>The one rule implemented today: "no more than N in the last window".</summary>
    public const string CountWithin = "count_within";

    /// <summary>Window applied when a caller asks for a count but supplies no span.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// A non-positive window is clamped up to this floor rather than treated as "no window":
    /// a malformed rule must not be able to switch the limit off.
    /// </summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound so a caller cannot force an unbounded ledger scan.</summary>
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// Audit category for policy index rows. Keeping it distinct from "Domain"/"SecurityAccess"
    /// lets the security timeline filter policy bookkeeping away from evidence in one predicate.
    /// </summary>
    public const string Category = "Policy";

    /// <summary>
    /// Policy events are recorded in the canonical audit ledger as
    /// <c>agent.policy.&lt;kind&gt;</c>. See the storage rationale on
    /// <c>CSweet.Infrastructure.Agents.AgentPolicyService</c>.
    /// </summary>
    public const string EventTypePrefix = "agent.policy.";

    /// <summary>Must match <c>AuditEvent.EventType</c> (configured with HasMaxLength(160)).</summary>
    public const int MaximumEventTypeLength = 160;

    /// <summary>Room for a kind once the event-type prefix is prepended.</summary>
    public static readonly int MaximumKindLength = MaximumEventTypeLength - EventTypePrefix.Length;

    /// <summary>Entity type stamped on the ledger row.</summary>
    public const string EntityType = "AgentPolicyEvent";

    /// <summary>
    /// Kinds are trimmed and lower-cased before they are stored or counted. Ordinal, case-sensitive
    /// comparison of raw caller input would let "Destructive" and "destructive" accumulate in two
    /// different windows; normalising on both sides is what makes the count trustworthy.
    /// </summary>
    public static string NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return string.Empty;
        var trimmed = kind.Trim();
        var lowered = trimmed.ToLowerInvariant();
        return lowered.Length <= MaximumKindLength ? lowered : lowered[..MaximumKindLength];
    }

    /// <summary>Maps a normalised kind to the ledger event type used for both writes and counts.</summary>
    public static string ToEventType(string normalizedKind) => EventTypePrefix + normalizedKind;

    /// <summary>Recovers the policy kind from a ledger event type, for read-only inspection.</summary>
    public static bool TryGetKind(string? eventType, out string kind)
    {
        kind = string.Empty;
        if (string.IsNullOrEmpty(eventType) ||
            !eventType.StartsWith(EventTypePrefix, StringComparison.Ordinal)) return false;
        kind = eventType[EventTypePrefix.Length..];
        return kind.Length > 0;
    }

    /// <summary>Clamps a requested window into <see cref="MinimumWindow"/>..<see cref="MaximumWindow"/>.</summary>
    public static TimeSpan ClampWindow(TimeSpan window)
    {
        if (window < MinimumWindow) return MinimumWindow;
        return window > MaximumWindow ? MaximumWindow : window;
    }

    /// <summary>Whole minutes, floored at one, for presentation and request echoes.</summary>
    public static int WindowMinutesOf(TimeSpan window)
    {
        var minutes = (int)Math.Round(ClampWindow(window).TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes < 1 ? 1 : minutes;
    }
}

/// <summary>Read-only inspection of recorded policy events for one tenant scope.</summary>
public sealed record AgentPolicyEventQuery(
    string? BusinessId,
    Guid? InstallationId,
    string? Kind,
    TimeSpan Window,
    int Limit = 100);

/// <summary>
/// Result of an inspection query. <see cref="Count"/> is the full windowed count (not the page
/// size) so an operator can compare it with the threshold that governs the installation.
/// </summary>
public sealed record AgentPolicyEventPage(
    string? BusinessId,
    Guid? InstallationId,
    int Count,
    int WindowMinutes,
    IReadOnlyList<AgentPolicyEvent> Events);

/// <summary>Body of <c>POST /api/security/agent-policy/evaluate</c>.</summary>
public sealed record AgentPolicyEvaluateRequest(
    string? BusinessId,
    Guid? InstallationId,
    string Kind,
    int WindowMinutes,
    int Maximum,
    CapabilityRiskClass Risk = CapabilityRiskClass.Destructive,
    DateTimeOffset? OccurredAt = null);

/// <summary>
/// A decision plus the observation that produced it, so a refusal is explainable in one response.
/// <see cref="Maximum"/> is the number of events permitted inside the window; the event being
/// evaluated is denied once the recorded count reaches it.
/// </summary>
public sealed record AgentPolicyEvaluation(
    bool Allowed,
    string? Rule,
    string Reason,
    int CountWithin,
    int Maximum,
    int WindowMinutes)
{
    public static AgentPolicyEvaluation From(
        AgentPolicyDecision decision,
        AgentPolicyThreshold threshold,
        int countWithin,
        TimeSpan window) =>
        new(decision.Allowed,
            decision.Rule,
            decision.Reason,
            countWithin,
            Math.Max(threshold.Maximum, 0),
            AgentPolicyRules.WindowMinutesOf(window));
}
