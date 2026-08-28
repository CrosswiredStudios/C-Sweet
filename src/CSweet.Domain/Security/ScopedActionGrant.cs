namespace CSweet.Domain.Security;

public enum GrantSubjectKind
{
    OrganizationUser,
    AgentInstallation,
    AutomationIdentity
}

public enum GrantScopeKind
{
    Organization,
    Team,
    Workstream,
    Board,
    WorkItem,
    Artifact
}

/// <summary>
/// An explicit action grant for a subject within one organization. Roles and
/// legacy permission levels may seed these records, but are not evaluated as
/// runtime authority by work-management services.
/// </summary>
public sealed class ScopedActionGrant
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public GrantSubjectKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    public string Action { get; set; } = string.Empty;
    public GrantScopeKind ScopeKind { get; set; }
    public Guid? ScopeId { get; set; }
    public bool CanDelegate { get; set; }
    public Guid? ParentGrantId { get; set; }
    public GrantSubjectKind GrantedBySubjectKind { get; set; }
    public Guid? GrantedBySubjectId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public ScopedActionGrant? ParentGrant { get; set; }
}
