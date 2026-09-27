using System.Text.Json;
using CSweet.Domain.Core;

namespace CSweet.Infrastructure.Core;

public static class ManagedActionApprovalAuthority
{
    public static bool RequiresManager(string? configurationJson)
    {
        try
        {
            using var configuration = JsonDocument.Parse(configurationJson ?? "{}");
            return !configuration.RootElement.TryGetProperty("approvalMode", out var value) ||
                (value.GetString() ?? "Manager Approval") == "Manager Approval";
        }
        catch (JsonException) { return true; }
    }

    public static bool CanDecide(OrganizationUser actor, Guid? managerId, string? configurationJson) =>
        RequiresManager(configurationJson)
            ? managerId == actor.Id
            : actor.PermissionLevel == OrganizationPermissionLevel.Owner;
}
