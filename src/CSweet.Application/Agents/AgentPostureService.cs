using System.Collections.Concurrent;
using CSweet.Contracts.Agents;

namespace CSweet.Application.Agents;

/// <summary>
/// The posture recorded for one agent installation, with the authority rules from
/// <see cref="AgentAuthority"/> resolved once so that the API, the chip and the broker never
/// re-derive "what may this agent do right now" independently.
/// </summary>
/// <param name="InstallationId">The agent installation this posture belongs to.</param>
/// <param name="Posture">The posture currently in force.</param>
/// <param name="ChangedAt">
/// When the posture was last set. <c>null</c> means the installation has never overridden its
/// posture and <see cref="IsDefault"/> is true; the effective posture is still
/// <see cref="AgentPostureService.Default"/>.
/// </param>
/// <param name="ChangedByApplicationUserId">The human who last changed the posture, when known.</param>
public sealed record AgentPostureAssignment(
    Guid InstallationId,
    AgentPosture Posture,
    DateTimeOffset? ChangedAt = null,
    Guid? ChangedByApplicationUserId = null)
{
    /// <summary>True when no posture has been stored for this installation yet.</summary>
    public bool IsDefault => ChangedAt is null;

    /// <summary>The operator-facing explanation of the rule, e.g. "writes need approval".</summary>
    public string Rule => AgentPostureRules.Describe(Posture);

    /// <summary>True when the posture permits acting on this risk class without a human in the loop.</summary>
    public bool Allows(CapabilityRiskClass risk) => AgentAuthority.Allows(risk, Posture);

    /// <summary>True when this risk class queues as an approval instead of proceeding.</summary>
    public bool RequiresApproval(CapabilityRiskClass risk) => AgentAuthority.RequiresApproval(risk, Posture);

    /// <summary>True when this risk class is refused outright rather than queued.</summary>
    public bool IsRefused(CapabilityRiskClass risk) => AgentAuthority.IsRefused(risk, Posture);
}

/// <summary>
/// The one vocabulary for posture across the API, the UI and the broker. The wording is derived
/// from <see cref="AgentAuthority"/> rather than restated as a table, so a change to the rule
/// cannot leave the copy describing the old rule. The Blazor chip must re-derive the same strings
/// (it can only reference CSweet.Contracts), so keep the two in step if the phrasing changes.
/// </summary>
public static class AgentPostureRules
{
    /// <summary>The postures in the order the chip and the API present them.</summary>
    public static IReadOnlyList<AgentPosture> All { get; } =
        [AgentPosture.Plan, AgentPosture.Manual, AgentPosture.Auto];

    /// <summary>The rule this posture applies to a write, in operator words.</summary>
    public static string Describe(AgentPosture posture)
    {
        const CapabilityRiskClass write = CapabilityRiskClass.Write;
        if (AgentAuthority.IsRefused(write, posture)) return "writes are refused";
        if (AgentAuthority.RequiresApproval(write, posture)) return "writes need approval";
        if (!AgentAuthority.Allows(CapabilityRiskClass.Destructive, posture)) return "reads only";
        return AgentAuthority.Allows(CapabilityRiskClass.Outward, posture)
            ? "writes and outward actions proceed without a prompt inside the granted scope"
            : "writes proceed without a prompt inside the granted scope";
    }

    /// <summary>A single-line title, e.g. "Manual - writes need approval".</summary>
    public static string Title(AgentPosture posture) => $"{posture} - {Describe(posture)}";
}

/// <summary>
/// The persistence seam for posture assignments.
/// <para>
/// Production uses the database-backed installation posture shared by API and AgentHost.
/// The in-memory implementation is available for isolated fixtures.
/// </para>
/// <para>
/// Implementations must be safe for concurrent readers, must never return a synthesised default
/// (a missing record is <c>null</c>), and must be registered as a singleton or scoped service that
/// resolves per call.
/// </para>
/// </summary>
public interface IAgentPostureStore
{
    /// <summary>The stored assignment, or <c>null</c> when this installation has no posture yet.</summary>
    ValueTask<AgentPostureAssignment?> FindAsync(Guid installationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the installation may still execute at all: it is enabled and sitting on an active
    /// revision. A disabled or rolled-back installation keeps its stored posture, so callers must ask
    /// this separately rather than treating a readable posture as permission to run.
    /// </summary>
    ValueTask<bool> IsActiveAsync(Guid installationId, CancellationToken cancellationToken = default);

    /// <summary>Writes the assignment, replacing any previous posture for the installation.</summary>
    ValueTask SaveAsync(AgentPostureAssignment assignment, CancellationToken cancellationToken = default);

    /// <summary>Removes any stored posture; true when something was removed.</summary>
    ValueTask<bool> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The in-memory seam implementation: one assignment per installation, last write wins.
/// Nothing is evicted because the working set is one record per installed agent.
/// </summary>
public sealed class InMemoryAgentPostureStore : IAgentPostureStore
{
    private readonly ConcurrentDictionary<Guid, AgentPostureAssignment> _assignments = [];

    /// <summary>How many installations currently carry a stored posture. Diagnostics only.</summary>
    public int Count => _assignments.Count;

    public ValueTask<AgentPostureAssignment?> FindAsync(
        Guid installationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _assignments.TryGetValue(installationId, out var assignment)
            ? ValueTask.FromResult<AgentPostureAssignment?>(assignment)
            : ValueTask.FromResult<AgentPostureAssignment?>(null);
    }

    public ValueTask SaveAsync(AgentPostureAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();
        _assignments[assignment.InstallationId] = assignment;
        return ValueTask.CompletedTask;
    }

    /// <summary>In-memory fixtures carry no install lifecycle, so every known installation is active.</summary>
    public ValueTask<bool> IsActiveAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> RemoveAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_assignments.TryRemove(installationId, out _));
    }
}

/// <summary>Read-only posture surface for callers that must not change it, such as the broker.</summary>
public interface IAgentPostureReader
{
    /// <summary>
    /// The posture in force for the installation, or <see cref="AgentPostureService.Default"/>
    /// when none was ever set. Never throws for an unknown installation.
    /// </summary>
    ValueTask<AgentPostureAssignment> GetAsync(Guid installationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// False when the installation is disabled or no longer on an active revision. A caller that
    /// enforces authority must refuse before consulting the posture when this is false.
    /// </summary>
    ValueTask<bool> IsActiveAsync(Guid installationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and writes the posture per agent installation.
/// <para>
/// Production registers a scoped database-backed <see cref="IAgentPostureStore"/> so the API and
/// AgentHost processes share one posture; <see cref="InMemoryAgentPostureStore"/> is for tests and
/// single-process tools only. The service is deliberately storage-agnostic.
/// </para>
/// </summary>
public sealed class AgentPostureService(IAgentPostureStore store, IAgentPostureAuthorization authorization, TimeProvider? clock = null)
    : IAgentPostureReader
{
    /// <summary>
    /// The posture an installation has until someone chooses otherwise. Manual is the product
    /// default: writes queue as approvals, nothing mutates without a human.
    /// </summary>
    public static AgentPosture Default => AgentPosture.Manual;

    private TimeProvider Clock => clock ?? TimeProvider.System;

    public async ValueTask<AgentPostureAssignment> GetAsync(
        Guid installationId, CancellationToken cancellationToken = default)
    {
        RequireInstallation(installationId);
        var stored = await store.FindAsync(installationId, cancellationToken).ConfigureAwait(false);
        return stored ?? new AgentPostureAssignment(installationId, Default);
    }

    public ValueTask<bool> IsActiveAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        RequireInstallation(installationId);
        return store.IsActiveAsync(installationId, cancellationToken);
    }

    /// <summary>
    /// Records the posture for an installation and returns the assignment now in force. The
    /// previous value is intentionally not consulted: last write wins, and the caller records
    /// the audit event.
    /// </summary>
    public async ValueTask<AgentPostureAssignment> SetAsync(
        Guid installationId,
        AgentPosture posture,
        Guid changedByApplicationUserId,
        bool administrator = false,
        CancellationToken cancellationToken = default)
    {
        RequireInstallation(installationId);
        if (!Enum.IsDefined(posture)) throw new ArgumentOutOfRangeException(nameof(posture), posture, "Unknown agent posture.");
        if (changedByApplicationUserId == Guid.Empty)
            throw new UnauthorizedAccessException("Changing an agent posture requires an authenticated human.");
        // The service is the public write surface, so it authorizes itself rather than trusting the caller.
        if (!await authorization.CanChangeAsync(installationId, changedByApplicationUserId, administrator, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("You may not change this agent's posture.");
        var assignment = new AgentPostureAssignment(installationId, posture, Clock.GetUtcNow(), changedByApplicationUserId);
        await store.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
        return assignment;
    }

    /// <summary>Clears the stored posture so the installation falls back to <see cref="Default"/>.</summary>
    public ValueTask<bool> ResetAsync(Guid installationId, CancellationToken cancellationToken = default)
    {
        RequireInstallation(installationId);
        return store.RemoveAsync(installationId, cancellationToken);
    }

    /// <summary>
    /// The authority decision for one capability invocation, expressed as the three outcomes the
    /// broker distinguishes: proceed, queue for approval, refuse.
    /// </summary>
    public static (bool Proceed, bool RequiresApproval, bool Refused) Decide(
        CapabilityRiskClass risk, AgentPosture posture) =>
        (AgentAuthority.Allows(risk, posture),
         AgentAuthority.RequiresApproval(risk, posture),
         AgentAuthority.IsRefused(risk, posture));

    private static void RequireInstallation(Guid installationId)
    {
        if (installationId == Guid.Empty)
            throw new ArgumentException("An agent installation identifier is required.", nameof(installationId));
    }
}
