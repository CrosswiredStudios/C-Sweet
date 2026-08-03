using CSweet.Contracts.Core;

namespace CSweet.UI.Services;

public static class BusinessNavigation
{
    public static Guid? OrganizationIdFromPath(string? path)
    {
        var segments = PathSegments(path);
        return segments.Length >= 2 &&
               string.Equals(segments[0], "organizations", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[1], out var id)
            ? id
            : null;
    }

    public static Guid? ResolveSelection(
        string? path,
        Guid? persistedId,
        IReadOnlyList<OrganizationResponse> businesses)
    {
        var routeId = OrganizationIdFromPath(path);
        if (routeId is not null && businesses.Any(x => x.Id == routeId))
        {
            return routeId;
        }

        if (persistedId is not null && businesses.Any(x => x.Id == persistedId))
        {
            return persistedId;
        }

        return businesses.FirstOrDefault()?.Id;
    }

    public static string SwitchDestination(string? currentPath, Guid businessId)
    {
        var segments = PathSegments(currentPath);
        if (segments.Length >= 3 &&
            string.Equals(segments[0], "organizations", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(segments[2], "employees", StringComparison.OrdinalIgnoreCase))
            {
                return $"/organizations/{businessId}/employees";
            }

            if (string.Equals(segments[2], "communications", StringComparison.OrdinalIgnoreCase))
            {
                return $"/organizations/{businessId}/communications";
            }

            if (string.Equals(segments[2], "approvals", StringComparison.OrdinalIgnoreCase))
            {
                return $"/organizations/{businessId}/approvals";
            }

            if (string.Equals(segments[2], "command-center", StringComparison.OrdinalIgnoreCase))
            {
                return $"/organizations/{businessId}/command-center";
            }

            if (string.Equals(segments[2], "analytics", StringComparison.OrdinalIgnoreCase))
            {
                return $"/organizations/{businessId}/analytics";
            }
        }

        return $"/organizations/{businessId}/command-center";
    }

    public static bool IsEmployeePath(string? path)
    {
        var segments = PathSegments(path);
        return segments.Length >= 3 &&
               string.Equals(segments[0], "organizations", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[2], "employees", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] PathSegments(string? path) =>
        (path ?? string.Empty).Trim('/').Split('?', '#')[0]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
}
