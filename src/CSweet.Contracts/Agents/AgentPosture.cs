namespace CSweet.Contracts.Agents;

/// <summary>
/// How much authority an agent session may exercise without a human in the loop.
/// Mirrors the posture model used by operator harnesses: a user sets a posture
/// once for a piece of work instead of approving capability-by-capability.
/// </summary>
public enum AgentPosture
{
    /// <summary>Read, reason and propose. Mutating capabilities are refused at the broker.</summary>
    Plan = 0,

    /// <summary>Mutating capabilities queue as approvals. The product default today.</summary>
    Manual = 1,

    /// <summary>Mutating capabilities proceed inside the granted scope without a prompt.</summary>
    Auto = 2
}

/// <summary>
/// The blast radius of a capability. Approval UI and broker enforcement both key off
/// this instead of treating every capability as equally dangerous.
/// </summary>
public enum CapabilityRiskClass
{
    /// <summary>No side effects: reads, searches, listings. Never needs approval.</summary>
    Read = 0,

    /// <summary>Creates or mutates state owned by the business.</summary>
    Write = 1,

    /// <summary>Deletes or irreversibly changes state.</summary>
    Destructive = 2,

    /// <summary>Leaves the instance: network egress, email, repository push, third-party API.</summary>
    Outward = 3
}

/// <summary>
/// The single decision function shared by the broker, the API and the UI so that
/// "what may this agent do right now" has one answer.
/// </summary>
public static class AgentAuthority
{
    /// <summary>True when the posture permits acting on a capability of this risk class.</summary>
    public static bool Allows(CapabilityRiskClass risk, AgentPosture posture) => posture switch
    {
        AgentPosture.Auto => true,
        AgentPosture.Manual => risk == CapabilityRiskClass.Read,
        AgentPosture.Plan => risk == CapabilityRiskClass.Read,
        _ => false
    };

    /// <summary>True when the action may proceed only after a human approves it.</summary>
    public static bool RequiresApproval(CapabilityRiskClass risk, AgentPosture posture) =>
        posture == AgentPosture.Manual && risk != CapabilityRiskClass.Read;

    /// <summary>True when the posture refuses the action outright rather than queuing it.</summary>
    public static bool IsRefused(CapabilityRiskClass risk, AgentPosture posture) =>
        posture == AgentPosture.Plan && risk != CapabilityRiskClass.Read;

    public static bool TryParse(string? value, out AgentPosture posture)
    {
        posture = AgentPosture.Manual;
        return value is not null && Enum.TryParse(value, ignoreCase: true, out posture) &&
            Enum.IsDefined(posture) && string.Equals(value.Trim(), posture.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
