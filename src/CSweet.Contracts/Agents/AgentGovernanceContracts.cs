namespace CSweet.Contracts.Agents;

/// <summary>
/// One recorded thing an agent did, used by temporal policy ("more than three
/// destructive operations in an hour"). Deliberately small: it is an index over
/// the audit trail, not a second copy of it.
/// </summary>
public sealed record AgentPolicyEvent(
    string Kind,
    Guid? InstallationId,
    string? BusinessId,
    CapabilityRiskClass Risk,
    DateTimeOffset OccurredAt);

/// <summary>The outcome of evaluating policy, surfaced so a refusal is explainable.</summary>
public sealed record AgentPolicyDecision(
    bool Allowed,
    string? Rule,
    string Reason)
{
    public static readonly AgentPolicyDecision Permit = new(true, null, "permitted");
}

/// <summary>
/// Windowed aggregation requested by a policy rule, e.g. "destructive executions
/// in the last hour for this installation".
/// </summary>
public sealed record AgentPolicyWindow(
    string Kind,
    Guid? InstallationId,
    string? BusinessId,
    TimeSpan Window);

/// <summary>A windowed count compared against a threshold.</summary>
public sealed record AgentPolicyThreshold(string Kind, TimeSpan Window, int Maximum);

/// <summary>
/// One delegation: a bounded handoff from one employee to another with an explicit
/// scope, inputs and return contract, instead of a request described in prose.
/// </summary>
public sealed record CreateDelegationRequest(
    string BusinessId,
    Guid FromInstallationId,
    Guid ToInstallationId,
    string Title,
    string Scope,
    IReadOnlyList<Guid> InputArtifactIds,
    string? ReturnContract)
{
    public Guid SourceConversationId { get; init; }
    public Guid SourceMessageId { get; init; }
    public string IdempotencyKey { get; init; } = "";
}

/// <summary>Delegation lifecycle: Pending, Accepted, Completed, Declined, Expired.</summary>
public sealed record DelegationResponse(
    Guid Id,
    Guid FromInstallationId,
    Guid ToInstallationId,
    string Title,
    string Scope,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public IReadOnlyList<Guid> InputArtifactIds { get; init; } = [];
    public string? ReturnContract { get; init; }
    public Guid? TrackedDelegationId { get; init; }
    public Guid? WorkItemId { get; init; }
    public long Revision { get; init; }
    public bool CanRespond { get; init; }
}

public sealed record DecideDelegationRequest(long Revision, string Summary, string IdempotencyKey);
