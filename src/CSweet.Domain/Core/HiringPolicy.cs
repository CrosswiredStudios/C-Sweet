namespace CSweet.Domain.Core;

public sealed class ChiefHiringPolicy
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public long Revision { get; set; } = 1;
    public string SettingsJson { get; set; } = "{}";
    public string SetupStage { get; set; } = "mode";
    public bool SetupComplete { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? SourceDecisionId { get; set; }
    public string Rationale { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class ChiefHiringPolicyRevision
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public long Revision { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset EffectiveAt { get; set; }
}
public sealed class HiringPlanDelegation
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid ResourceChangeRequestId { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
