namespace CSweet.Domain.Core;

public sealed class BusinessSuggestion
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Key { get; set; } = "hire-chief-of-staff";
    public string Kind { get; set; } = "HireChiefOfStaff";
    public string Status { get; set; } = "Pending";
    public long Revision { get; set; } = 1;
    public Guid? DismissedByOrganizationUserId { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Source ownership exists before a delivery team or board is approved.</summary>
public sealed class ProjectRepositoryAssociation
{
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid ImportSourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ImportedProjectProfile
{
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ImportSourceId { get; set; }
    public string Status { get; set; } = "NotStarted";
    public string InferredJson { get; set; } = "{}";
    public string AcceptedJson { get; set; } = "{}";
    public string? Error { get; set; }
    public Guid? AgentWorkItemId { get; set; }
    public Guid? RequestedByApplicationUserId { get; set; }
    public Guid? AcceptedByOrganizationUserId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public long AnalysisRevision { get; set; }
    public Guid? AnalysisDraftId { get; set; }
}
