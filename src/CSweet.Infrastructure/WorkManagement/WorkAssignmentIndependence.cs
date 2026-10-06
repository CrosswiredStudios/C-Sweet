namespace CSweet.Infrastructure.WorkManagement;

/// <summary>Authors of code and artifacts cannot independently review their own delivery.</summary>
public static class WorkAssignmentIndependence
{
    public static IReadOnlyList<string> FindSelfReviewStages(
        IEnumerable<(string StageKey, Guid? InstallationId, Guid? EmployeeId, string? RoleKey)> assignments)
    {
        var all = assignments.ToArray();
        var authors = all.Where(a => a.StageKey is "development" or "specialist-execution").ToArray();
        return all.Where(a => a.StageKey is "technical-review" or "quality" or "merge-decision")
            .Where(a => authors.Any(author =>
                a.InstallationId.HasValue && a.InstallationId == author.InstallationId ||
                a.EmployeeId.HasValue && a.EmployeeId == author.EmployeeId))
            .Select(a => a.StageKey).Distinct(StringComparer.Ordinal).ToArray();
    }
}
