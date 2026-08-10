namespace CSweet.Contracts.Core;

public sealed record OrganizationUserResponse(
    Guid Id,
    Guid OrganizationId,
    Guid? ReportsToOrganizationUserId,
    Guid? RoleId,
    Guid? WorkerId,
    string DisplayName,
    string? Email,
    int EmployeeType,
    int PermissionLevel,
    DateTimeOffset CreatedAt)
{
    public Guid? ApplicationUserId { get; init; }
    public Guid? AgentInstallationId { get; init; }
    public Guid? InitialConversationId { get; init; }
    public bool SupportsAgentConfiguration { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTimeOffset? ArchivedAt { get; init; }
    public long Revision { get; init; } = 1;
}
