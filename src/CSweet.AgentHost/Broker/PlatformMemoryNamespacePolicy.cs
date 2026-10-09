using CSweet.Memory;
using CSweet.Infrastructure.Core;

namespace CSweet.AgentHost.Broker;

public enum PlatformMemoryAction { Read, Propose, Manage }

/// <summary>
/// Validates the complete namespace shape before canonical storage identity is used.
/// Installation-scoped legacy data remains separate; this does not migrate or promote it.
/// </summary>
internal static class PlatformMemoryNamespacePolicy
{
    internal static MemoryNamespace Resolve(AgentSession session, MemoryPartition partition, PlatformMemoryAction action)
    {
        if (!IsCanonicalId(session.MemoryTenantId) || !IsCanonicalId(session.MemoryEmployeeId) ||
            !IsCanonicalId(session.InstallationId) || partition.TenantId != session.MemoryTenantId ||
            (partition.ApplicationId != "csweet" && partition.ApplicationId != session.InstallationId))
            throw Denied();

        var tenant = session.MemoryTenantId!;
        var employee = session.MemoryEmployeeId!;
        var app = partition.ApplicationId;
        MemoryNamespace expected;
        if (partition.CustomNamespace == "organization")
            expected = EmployeeMemoryNamespaces.Organization(tenant, app);
        else if (partition.CustomNamespace == $"employee:{employee}")
            expected = EmployeeMemoryNamespaces.Employee(tenant, employee, app);
        else if (IsCanonicalId(partition.UserId) && partition.CustomNamespace == $"relationship:{employee}:{partition.UserId}")
            expected = EmployeeMemoryNamespaces.UserRelationship(tenant, employee, partition.UserId!, app);
        else if (ReadAudienceId(partition.CustomNamespace, "team:") is { } team)
            expected = EmployeeMemoryNamespaces.Team(tenant, team, app);
        else if (ReadAudienceId(partition.CustomNamespace, "role:") is { } role)
            expected = EmployeeMemoryNamespaces.Role(tenant, role, app);
        else if (MemoryScopedAudienceAuthorization.Resolve(partition) is { } scoped)
            expected = scoped;
        else
            // Arbitrary custom scopes and installation-scoped case/conversation audiences have no policy.
            throw Denied();

        // Compare fields, never Key: distinct legacy field assignments can have the same key.
        if (partition != expected.Partition) throw Denied();
        if (action == PlatformMemoryAction.Manage && expected.Audience is not
            (MemoryAudienceType.Employee or MemoryAudienceType.UserRelationship)) throw Denied();
        return expected;
    }

    private static string? ReadAudienceId(string? value, string prefix) =>
        value?.StartsWith(prefix, StringComparison.Ordinal) == true && IsCanonicalId(value[prefix.Length..])
            ? value[prefix.Length..] : null;

    private static bool IsCanonicalId(string? value) =>
        Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;

    internal static UnauthorizedAccessException Denied() => new("The caller cannot access this memory namespace.");
}
