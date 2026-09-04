namespace CSweet.Domain.Core;

public enum ResourceChangeRequestStatus
{
    Pending,
    Approved,
    RevisionRequested,
    Rejected,
    Superseded
}

public sealed class ResourceChangeRequestRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequesterOrganizationUserId { get; set; }
    public Guid RequesterInstallationId { get; set; }
    public Guid ManagerOrganizationUserId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid ChatTurnId { get; set; }
    public Guid ConversationMessageId { get; set; }
    public Guid? SupersedesRequestId { get; set; }
    public string ProductGoal { get; set; } = string.Empty;
    public Guid? TeamId { get; set; }
    public Guid? WorkstreamId { get; set; }
    public long? ExpectedTeamRevision { get; set; }
    public string? TeamKey { get; set; }
    public string? TeamName { get; set; }
    public string? TeamDescription { get; set; }
    public string Rationale { get; set; } = string.Empty;
    public long ContextRevision { get; set; }
    public string AssumptionsJson { get; set; } = "[]";
    public string ConstraintsJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "[]";
    public string AlternativesConsideredJson { get; set; } = "[]";
    public string? ExpectedEffect { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public ResourceChangeRequestStatus Status { get; set; } = ResourceChangeRequestStatus.Pending;
    public string DeliveryStatus { get; set; } = "Pending";
    public string? DecisionComment { get; set; }
    public Guid? DecidedByOrganizationUserId { get; set; }
    public string? DecisionIdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public ICollection<ResourceChangeRoleRecord> Roles { get; set; } = new List<ResourceChangeRoleRecord>();
}

public sealed class ResourceChangeRoleRecord
{
    public Guid Id { get; set; }
    public Guid ResourceChangeRequestId { get; set; }
    public string RoleKey { get; set; } = string.Empty;
    public string RoleCategoryKey { get; set; } = string.Empty;
    public string PreferredSpecializationKeysJson { get; set; } = "[]";
    public string Team { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public int Headcount { get; set; }
    public int Priority { get; set; }
    public string Timing { get; set; } = string.Empty;
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public bool HumanRequired { get; set; }
    public Guid? ReportsToOrganizationUserId { get; set; }
    public string? ReportsToRoleKey { get; set; }
    public string ChangeKind { get; set; } = "Add";
    public bool IsDesired { get; set; } = true;
    public string? PreviousRoleJson { get; set; }
    public Guid? TeamId { get; set; }

    public ResourceChangeRequestRecord? Request { get; set; }
}
