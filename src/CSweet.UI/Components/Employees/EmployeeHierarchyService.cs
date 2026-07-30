using CSweet.UI.Components.Employees.Models;

namespace CSweet.UI.Components.Employees;

public static class EmployeeHierarchyService
{
    public static Guid? InitialFocus(IReadOnlyList<EmployeeViewModel> employees) =>
        employees.FirstOrDefault(x => x.IsSelf)?.Id ??
        employees.Where(x => !x.ManagerId.HasValue).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Id ??
        employees.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Id;

    public static EmployeeGraphModel Build(
        IReadOnlyList<EmployeeViewModel> employees,
        Guid? focusId,
        int degrees)
    {
        const double nodeWidth = 260;
        const double nodeHeight = 144;
        if (employees.Count == 0)
        {
            return new EmployeeGraphModel([], [], 720, 360, nodeWidth, nodeHeight);
        }

        var byId = employees.ToDictionary(x => x.Id);
        var actualFocus = focusId.HasValue && byId.ContainsKey(focusId.Value)
            ? focusId.Value
            : InitialFocus(employees)!.Value;
        var visibleIds = WithinDegrees(employees, actualFocus, Math.Clamp(degrees, 1, 3));
        var visible = employees.Where(x => visibleIds.Contains(x.Id)).ToArray();
        var levels = visible
            .GroupBy(x => Depth(x, byId))
            .OrderBy(x => x.Key)
            .ToArray();
        const double xSpacing = nodeWidth + 38;
        const double ySpacing = nodeHeight + 54;
        const double sidePadding = nodeWidth / 2 + 30;
        const double topPadding = nodeHeight / 2 + 30;
        var maxCount = Math.Max(1, levels.Max(x => x.Count()));
        var width = Math.Max(720, (maxCount - 1) * xSpacing + sidePadding * 2);
        var nodes = new List<EmployeeGraphLayoutNode>();

        foreach (var level in levels)
        {
            var ordered = level.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var contentWidth = (ordered.Length - 1) * xSpacing;
            var startX = (width - contentWidth) / 2;
            for (var index = 0; index < ordered.Length; index++)
            {
                nodes.Add(new EmployeeGraphLayoutNode(
                    ordered[index],
                    startX + index * xSpacing,
                    topPadding + (level.Key - levels[0].Key) * ySpacing,
                    level.Key));
            }
        }

        var nodesById = nodes.ToDictionary(x => x.Employee.Id);
        var edges = nodes
            .Where(x => x.Employee.ManagerId.HasValue && nodesById.ContainsKey(x.Employee.ManagerId.Value))
            .Select(x => new EmployeeGraphLayoutEdge(nodesById[x.Employee.ManagerId!.Value], x))
            .ToArray();
        var height = Math.Max(360, topPadding * 2 + Math.Max(0, levels.Length - 1) * ySpacing);
        return new EmployeeGraphModel(nodes, edges, width, height, nodeWidth, nodeHeight);
    }

    private static HashSet<Guid> WithinDegrees(
        IReadOnlyList<EmployeeViewModel> employees,
        Guid focusId,
        int degrees)
    {
        var adjacency = employees.ToDictionary(x => x.Id, _ => new HashSet<Guid>());
        foreach (var employee in employees)
        {
            if (employee.ManagerId.HasValue && adjacency.ContainsKey(employee.ManagerId.Value))
            {
                adjacency[employee.Id].Add(employee.ManagerId.Value);
                adjacency[employee.ManagerId.Value].Add(employee.Id);
            }
        }

        var visited = new HashSet<Guid> { focusId };
        var queue = new Queue<(Guid Id, int Distance)>();
        queue.Enqueue((focusId, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Distance >= degrees) continue;
            foreach (var neighbor in adjacency[current.Id].Order())
            {
                if (visited.Add(neighbor)) queue.Enqueue((neighbor, current.Distance + 1));
            }
        }
        return visited;
    }

    private static int Depth(EmployeeViewModel employee, IReadOnlyDictionary<Guid, EmployeeViewModel> byId)
    {
        var depth = 0;
        var current = employee;
        var visited = new HashSet<Guid> { employee.Id };
        while (current.ManagerId.HasValue && byId.TryGetValue(current.ManagerId.Value, out var manager))
        {
            if (!visited.Add(manager.Id)) return 0;
            depth++;
            current = manager;
        }
        return depth;
    }
}
