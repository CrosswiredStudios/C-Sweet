using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Core;

public sealed record TeamMemberInput(
    Guid OrganizationUserId,
    Guid? TeamRoleId = null);

public sealed record CreateTeamRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: MaxLength(2048)] string? Description,
    Guid LeadOrganizationUserId,
    IReadOnlyList<TeamMemberInput>? Members = null);

public sealed record UpdateTeamRequest(
    [property: Required, MaxLength(160)] string Name,
    [property: MaxLength(2048)] string? Description,
    Guid LeadOrganizationUserId,
    long ExpectedRevision);

public sealed record UpsertTeamMembershipRequest(
    Guid? TeamRoleId,
    long ExpectedRevision);

public sealed record TeamRevisionRequest(long ExpectedRevision);

public sealed record TeamMembershipResponse(
    Guid Id,
    Guid OrganizationUserId,
    string DisplayName,
    string EmployeeType,
    Guid? TeamRoleId,
    string? TeamRoleName,
    bool IsLead,
    DateTimeOffset JoinedAt,
    DateTimeOffset? EndedAt);

public sealed record TeamSummaryResponse(
    Guid Id,
    string TeamKey,
    string Name,
    string Description,
    Guid LeadOrganizationUserId,
    string LeadDisplayName,
    long Revision,
    bool IsArchived,
    int ActiveMemberCount,
    int HumanMemberCount,
    int AgentMemberCount,
    IReadOnlyList<TeamMembershipResponse> Members)
{
    public Guid? WorkstreamId { get; init; }
    public Guid? BoardId { get; init; }
}

public sealed record TeamDetailResponse(
    TeamSummaryResponse Team,
    Guid? WorkstreamId = null,
    Guid? BoardId = null);

public sealed record TeamDirectoryResponse(
    Guid CurrentOrganizationUserId,
    bool CanManageTeams,
    IReadOnlyList<TeamSummaryResponse> Teams);
