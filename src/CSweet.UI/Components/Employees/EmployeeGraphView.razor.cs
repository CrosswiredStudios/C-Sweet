using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;

namespace CSweet.UI.Components.Employees;

public partial class EmployeeGraphView
{
    private readonly HashSet<Guid> _collapsed = [];
    [Parameter] public IReadOnlyList<EmployeeViewModel> Employees { get; set; } = [];
    protected EmployeeGraphModel Graph { get; private set; } = new([], [], 720, 360, 260, 144);
    protected override void OnParametersSet()
    {
        _collapsed.RemoveWhere(id => !Employees.Any(employee => employee.Id == id));
        Rebuild();
    }
    private void Rebuild() => Graph = EmployeeHierarchyService.BuildOrganization(Employees, _collapsed);
    private void ToggleBranch(Guid id)
    {
        if (!_collapsed.Add(id)) _collapsed.Remove(id);
        Rebuild();
    }
    private void ExpandAll() { _collapsed.Clear(); Rebuild(); }
}
