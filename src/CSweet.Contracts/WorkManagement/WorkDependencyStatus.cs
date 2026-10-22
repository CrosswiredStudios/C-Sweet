namespace CSweet.Contracts.WorkManagement;

/// <summary>Current prerequisites, separate from a ticket's workflow column.</summary>
public sealed record WorkDependencyState(Guid Id, string Label, string Status, IReadOnlyList<Guid> Dependencies);

public static class WorkDependencyStatus
{
    public static IReadOnlyList<WorkDependencyState> UnmetRoots(Guid itemId, IReadOnlyDictionary<Guid, WorkDependencyState> items)
    {
        var result = new Dictionary<Guid, WorkDependencyState>();
        var visited = new HashSet<Guid> { itemId };
        void Visit(Guid id)
        {
            if (!visited.Add(id)) return;
            if (!items.TryGetValue(id, out var item))
            {
                result[id] = new(id, "Prerequisite outside this view", "Unknown", []);
                return;
            }
            if (item.Status == "Completed") return;
            var pending = item.Dependencies.Where(x => !items.TryGetValue(x, out var dependency) || dependency.Status != "Completed").ToArray();
            if (pending.Length == 0 || item.Status is "Blocked" or "Failed" or "Cancelled") result[id] = item;
            else foreach (var dependency in pending) Visit(dependency);
        }
        if (items.TryGetValue(itemId, out var subject))
        {
            foreach (var dependency in subject.Dependencies) Visit(dependency);
            // Invalid cycles must never appear ready.
            if (result.Count == 0 && subject.Dependencies.Any(x => !items.TryGetValue(x, out var dependency) || dependency.Status != "Completed"))
                result[itemId] = new(itemId, "Unresolved prerequisite cycle", "Unknown", []);
        }
        return result.Values.OrderBy(x => x.Label, StringComparer.Ordinal).ToArray();
    }
}
