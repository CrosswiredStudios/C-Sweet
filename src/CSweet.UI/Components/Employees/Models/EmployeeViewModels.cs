using CSweet.Contracts.Core;

namespace CSweet.UI.Components.Employees.Models;

public enum EmployeeViewKind
{
    Graph,
    Teams,
    Directory
}

public enum EmployeeTypeFilter
{
    All,
    Human,
    Agent
}

public enum EmployeeRuntimeStatus
{
    NotTracked,
    Checking,
    Online,
    Transitional,
    Offline,
    Failed
}

public enum EmployeeAction
{
    OpenChat,
    StartRuntime,
    StopRuntime,
    OpenConsole,
    Configure,
    OpenMemory,
    ChangeRole,
    Fire
}

public sealed record EmployeeActionRequest(EmployeeAction Action, Guid EmployeeId);

public sealed record EmployeeTeamViewModel(
    Guid TeamId,
    string TeamName,
    string? TeamRole,
    bool IsLead,
    bool IsArchived,
    string Color);

public sealed record EmployeeViewModel(
    OrganizationUserResponse Source,
    string Name,
    string Initials,
    string RoleLabel,
    string? RoleName,
    string? ManagerName,
    bool IsAgent,
    bool IsSelf,
    EmployeeRuntimeStatus Status,
    int DirectReportCount,
    IReadOnlyList<Guid> DirectReportIds,
    bool CanStartRuntime,
    bool CanStopRuntime,
    bool IsRuntimeBusy,
    IReadOnlyList<EmployeeAction> Actions)
{
    public Guid Id => Source.Id;
    public Guid? RoleId => Source.RoleId;
    public Guid? ManagerId => Source.ReportsToOrganizationUserId;
    public IReadOnlyList<EmployeeTeamViewModel> Teams { get; init; } = [];
}

public sealed record EmployeeDirectoryFilter(
    string Search = "",
    string Role = "all",
    EmployeeTypeFilter Type = EmployeeTypeFilter.All,
    EmployeeRuntimeStatus? Status = null,
    string Team = "all")
{
    public bool IsClear => string.IsNullOrWhiteSpace(Search) &&
        string.Equals(Role, "all", StringComparison.Ordinal) &&
        string.Equals(Team, "all", StringComparison.Ordinal) &&
        Type == EmployeeTypeFilter.All &&
        Status is null;
}

public sealed record EmployeeGraphLayoutNode(EmployeeViewModel Employee, double X, double Y, int Level);

public sealed record EmployeeGraphLayoutEdge(EmployeeGraphLayoutNode From, EmployeeGraphLayoutNode To);

public sealed record EmployeeGraphModel(
    IReadOnlyList<EmployeeGraphLayoutNode> Nodes,
    IReadOnlyList<EmployeeGraphLayoutEdge> Edges,
    double Width,
    double Height,
    double NodeWidth,
    double NodeHeight);
