namespace CSweet.Domain.Setup;

public sealed class AgentHarnessPolicy
{
    public Guid Id { get; set; } // Installation ID
    public long Revision { get; set; }
    public string PolicyJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class BusinessSkillRevision
{
    public Guid Id { get; set; }
    public string BusinessId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string Digest { get; set; } = "";
    public string Instructions { get; set; } = "";
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// One account per canonical root task, shared by installations, retries and delegated descendants.
public sealed class HarnessTaskBudget
{
    public string Id { get; set; } = "";
    public long? TokenLimit { get; set; }
    public decimal? CostLimit { get; set; }
    public bool HasUnpricedUsage { get; set; }
    public long ChargedTokens { get; set; }
    public decimal ChargedCost { get; set; }
}

public sealed class HarnessBudgetReservation
{
    public Guid Id { get; set; }
    public string BudgetId { get; set; } = "";
    public Guid RunId { get; set; }
    public long Tokens { get; set; }
    public decimal Cost { get; set; }
    public bool Settled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
