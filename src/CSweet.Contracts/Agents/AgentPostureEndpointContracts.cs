namespace CSweet.Contracts.Agents;

/// <summary>The request/response contracts for the posture surface.</summary>
/// <param name="Posture">"Plan", "Manual" or "Auto". The wire format is the name, not the ordinal.</param>
public sealed record SetAgentPostureRequest(string? Posture);

/// <summary>
/// The posture in force for one installation. <see cref="Posture"/> is sent as a name so that the
/// chip and the API speak the same vocabulary without a converter on either side.
/// </summary>
public sealed record AgentPostureResponse(
    Guid InstallationId,
    string Posture,
    string Rule,
    string Title,
    bool IsDefault,
    DateTimeOffset? ChangedAt,
    Guid? ChangedByApplicationUserId,
    string AgentId,
    string BusinessId);

