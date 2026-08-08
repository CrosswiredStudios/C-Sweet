using System.ComponentModel.DataAnnotations;

namespace CSweet.Contracts.Core;

public sealed record CreateOrganizationUserRequest(
    [Required] string DisplayName,
    string? Email,
    int PermissionLevel,
    int EmployeeType = 0,
    Guid? RoleId = null,
    Guid? WorkerId = null,
    Guid? ReportsToOrganizationUserId = null,
    IReadOnlyList<Guid>? ManagedOrganizationUserIds = null,
    Guid? AgentInstallationId = null,
    Guid? AgentDefinitionId = null);
