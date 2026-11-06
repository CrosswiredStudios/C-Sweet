using CSweet.Contracts.Agents;

namespace CSweet.Application.Security;

/// <summary>
/// Windowed temporal policy: "deny when this installation performed more than N destructive
/// operations in the last hour".
/// <para>
/// The service is append-mostly and process-wide. All three write/read members are safe to call
/// concurrently from any number of broker slots and HTTP requests; each call takes its own
/// database scope, so nothing here shares a <c>DbContext</c> across await points.
/// </para>
/// <para>
/// Ordering matters. <see cref="CountWithinAsync"/> and <see cref="EvaluateAsync"/> observe what
/// has already been recorded, so a caller that checks and then records must be aware that another
/// caller can interleave between the two steps. <see cref="TryAdmitAsync"/> closes that gap for a
/// business across PostgreSQL hosts with a transaction-scoped admission lock held through the
/// sealed audit append. In-memory fixtures use bounded process-local gates.
/// </para>
/// </summary>
public interface IAgentPolicyService
{
    /// <summary>
    /// Appends one event to the windowed counter. Intended for destructive/outward executions
    /// (low frequency), not for every capability call. The append is durable; it is intentionally
    /// not awaited by the broker's hot polling paths.
    /// </summary>
    Task RecordAsync(AgentPolicyEvent policyEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Events of <see cref="AgentPolicyWindow.Kind"/> inside the window for the scope's tenant,
    /// optionally narrowed to one installation. Returns 0 when the scope cannot be resolved to a
    /// tenant: a count is never borrowed from another tenant.
    /// </summary>
    Task<int> CountWithinAsync(AgentPolicyWindow window, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates a <c>count_within</c> rule against the candidate event's scope. The candidate is
    /// <b>not</b> recorded, and a denial is not recorded either (denial evidence belongs to the
    /// broker's own audit record). Permits when the recorded count is below
    /// <see cref="AgentPolicyThreshold.Maximum"/>, i.e. with Maximum 3 the first three events are
    /// permitted and the fourth is denied.
    /// </summary>
    Task<AgentPolicyDecision> EvaluateAsync(
        AgentPolicyThreshold threshold,
        AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="EvaluateAsync"/> but returns the observed count and window alongside the
    /// decision, for API responses and logs. One round trip instead of two.
    /// </summary>
    Task<AgentPolicyEvaluation> CheckAsync(
        AgentPolicyThreshold threshold,
        AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check-then-record as one operation: counts under a per-business database admission lock, and records the
    /// candidate only when the decision permits it. This is the path enforcement should use;
    /// <see cref="EvaluateAsync"/> remains available for dry runs.
    /// </summary>
    Task<AgentPolicyDecision> TryAdmitAsync(
        AgentPolicyThreshold threshold,
        AgentPolicyEvent candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only inspection of the events a window would count.</summary>
    Task<AgentPolicyEventPage> BrowseAsync(
        AgentPolicyEventQuery query,
        CancellationToken cancellationToken = default);
}
