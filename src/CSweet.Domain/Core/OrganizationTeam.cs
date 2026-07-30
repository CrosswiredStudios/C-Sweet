namespace CSweet.Domain.Core;

public sealed class OrganizationTeam
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string TeamKey { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid LeadOrganizationUserId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Organization? Organization { get; set; }
    public OrganizationUser? LeadOrganizationUser { get; set; }
    public ICollection<TeamMembership> Memberships { get; set; } = [];
}

public sealed class TeamMembership
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TeamId { get; set; }
    public Guid OrganizationUserId { get; set; }
    public Guid? TeamRoleId { get; set; }

    // Set to OrganizationUserId for agents and null for humans. The unique
    // index enforces the lifetime one-team boundary for an agent employee.
    public Guid? ExclusiveAgentEmployeeId { get; set; }

    public string SourceType { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public OrganizationTeam? Team { get; set; }
    public OrganizationUser? OrganizationUser { get; set; }
    public Role? TeamRole { get; set; }
}
