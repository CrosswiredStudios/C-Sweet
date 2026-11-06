using System.Text.Json;

namespace CSweet.Domain.Core;

/// <summary>
/// Lifecycle of a delegation packet. <see cref="Declined"/> and <see cref="Expired"/> are
/// terminal without work; <see cref="Completed"/> is terminal with work returned.
/// </summary>
public enum DelegationStatus
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Completed = 3,
    Expired = 4
}

/// <summary>
/// A bounded handoff from one employee to another: who asked, who owns it, the scope,
/// the artifacts that are inputs, and the contract for what comes back. Deliberately
/// small so a delegation reads like a work item instead of a paragraph of prose.
/// </summary>
public sealed class Delegation
{
    public Guid Id { get; set; }
    public Guid? TrackedDelegationId { get; set; }

    /// <summary>
    /// Business scope. Stored as the business id string, matching
    /// <see cref="CSweet.Domain.Setup.AgentInstallation.BusinessId"/> (the organization id in "D" form).
    /// </summary>
    public string BusinessId { get; set; } = "default";

    /// <summary>The employee (agent installation) asking for the work.</summary>
    public Guid FromInstallationId { get; set; }

    /// <summary>The employee (agent installation) the work was handed to.</summary>
    public Guid ToInstallationId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>What is in scope. The whole point of the packet: no prose, no negotiation.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// Artifact ids that are the inputs. Stored as a JSON array string, following the
    /// existing <c>*IdsJson</c> convention in this codebase; use
    /// <see cref="InputArtifactIds"/> / <see cref="SetInputArtifactIds"/> to read and write it.
    /// </summary>
    public string InputArtifactIdsJson { get; set; } = "[]";

    /// <summary>What the delegating employee expects back, and in what shape.</summary>
    public string? ReturnContract { get; set; }

    public DelegationStatus Status { get; set; } = DelegationStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Which employee returned the work, when it differs from <see cref="ToInstallationId"/>.
    /// A soft reference (no foreign key), matching how <see cref="Approval.DecidedByAgentInstallationId"/>
    /// keeps attribution without blocking installation removal.
    /// </summary>
    public Guid? CompletedByInstallationId { get; set; }

    /// <summary>Convenience view over <see cref="InputArtifactIdsJson"/>; never mapped as a column.</summary>
    public IReadOnlyList<Guid> InputArtifactIds
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InputArtifactIdsJson)) return [];
            try
            {
                return JsonSerializer.Deserialize<Guid[]>(InputArtifactIdsJson) ?? [];
            }
            catch (JsonException)
            {
                // A corrupt packet must not take down a list view.
                return [];
            }
        }
    }

    /// <summary>Replaces the input artifact list, de-duplicated and deterministically ordered.</summary>
    public void SetInputArtifactIds(IEnumerable<Guid> artifactIds) =>
        InputArtifactIdsJson = JsonSerializer.Serialize(artifactIds.Distinct().Order().ToArray());

    /// <summary>True once the packet can no longer change state.</summary>
    public bool IsSettled => Status is DelegationStatus.Declined or DelegationStatus.Completed or DelegationStatus.Expired;
}
