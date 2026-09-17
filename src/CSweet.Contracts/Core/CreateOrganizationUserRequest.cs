using System.ComponentModel.DataAnnotations;
using System.Text.Json;

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
    Guid? AgentDefinitionId = null)
{
    /// <summary>Canonical high-level role category expected by a governed hiring workflow.</summary>
    public string? RoleCategoryKey { get; init; }
    /// <summary>Explicit settings for this new employee, persisted before runtime activation.</summary>
    public IReadOnlyDictionary<string, JsonElement> ConfigurationOverrides { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
