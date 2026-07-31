using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.Llm;
using CSweet.Domain.Core;
using CSweet.UI.Components.Employees;
using CSweet.UI.Components.Employees.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace CSweet.UI.Pages;

public partial class Employees
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    [Parameter]
    public Guid OrganizationId { get; set; }
    [SupplyParameterFromQuery(Name = "tab")]
    public string? Tab { get; set; }
    [SupplyParameterFromQuery(Name = "recommendation")]
    public Guid? Recommendation { get; set; }

    private OrganizationResponse? _organization;
    private IReadOnlyList<OrganizationUserResponse> _employees = [];
    private IReadOnlyList<RoleResponse> _roles = [];
    private IReadOnlyList<WorkerResponse> _workers = [];
    private bool _loading = true;
    private string? _errorMessage;
    private string? _actionError;
    private bool _hireDialogOpen;
    private bool _fireDialogOpen;
    private bool _roleDialogOpen;
    private bool _saving;
    private string _hireName = string.Empty;
    private string? _hireEmail;
    private int _hireEmployeeType = 1;
    private string? _hireAgentKey;
    private IReadOnlyList<AgentInstallationResponse> _agentInstallations = [];
    private IReadOnlyList<LlmProviderProfileResponse> _providerProfiles = [];
    private Guid? _hireManagerId;
    private readonly HashSet<Guid> _managedEmployeeIds = [];
    private readonly HashSet<Guid> _hireTeamIds = [];
    private readonly Dictionary<Guid, Guid?> _hireTeamRoleIds = [];
    private OrganizationUserResponse? _employeeToFire;
    private OrganizationUserResponse? _roleEmployee;
    private Guid? _selectedRoleId;
    private OrganizationUserResponse? _configurationEmployee;
    private AgentConfigurationSchemaResponse? _configurationSchema;
    private AgentRuntimeReadinessResponse? _configurationRuntime;
    private readonly Dictionary<Guid, AgentRuntimeReadinessResponse> _runtimeStatuses = [];
    private readonly Dictionary<string, object?> _configurationValues = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, IReadOnlyList<string>> _providerModels = [];
    private readonly HashSet<Guid> _loadingProviderModels = [];
    private readonly Dictionary<Guid, string> _providerModelErrors = [];
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _configurationCts;
    private CancellationTokenSource? _runtimeStatusCts;
    private Guid? _managingRuntimeInstallationId;
    private bool _runtimeConsoleOpen;
    private bool _loadingRuntimeConsole;
    private OrganizationUserResponse? _runtimeConsoleEmployee;
    private IReadOnlyList<AgentRuntimeRunResponse> _runtimeRuns = [];
    private string? _runtimeConsoleError;
    private bool _configurationDialogOpen;
    private bool _loadingConfiguration;
    private bool _savingConfiguration;
    private string? _configurationError;
    private string? _configurationMessage;
    private readonly DialogOptions _dialogOptions = new() { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true };
    private readonly DialogOptions _runtimeConsoleOptions = new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseButton = true };
    private EmployeeViewKind _activeView = EmployeeViewKind.Graph;
    private Guid? _selectedEmployeeId;
    private Guid? _focusedTeamId;
    private int _graphDegrees = 2;
    private EmployeeDirectoryFilter _directoryFilter = new();
    private HiringDashboardResponse? _hiringDashboard;
    private TeamDirectoryResponse _teamDirectory = new(Guid.Empty, false, []);
    private bool _teamDialogOpen;
    private bool _teamMemberDialogOpen;
    private bool _teamConfirmDialogOpen;
    private bool _teamMutationBusy;
    private string? _teamMutationError;
    private TeamSummaryResponse? _editingTeam;
    private string _teamName = string.Empty;
    private string? _teamDescription;
    private Guid? _teamLeadId;
    private Guid? _teamMemberId;
    private Guid? _teamMemberRoleId;
    private readonly HashSet<Guid> _teamInitialMemberIds = [];
    private readonly Dictionary<Guid, Guid?> _teamInitialRoles = [];
    private bool _teamRevisionConflict;
    private bool _teamRevisionReviewed;
    private TeamUiActionRequest? _pendingTeamAction;
    private string _teamConfirmTitle = string.Empty;
    private string _teamConfirmMessage = string.Empty;
    private bool _resourceDecisionBusy;
    private string? _resourceFeedback;
    private bool IsHiringTab => string.Equals(Tab, "hiring", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<EmployeeViewModel> PresentedEmployees => EmployeePresentationService.Build(
        _employees,
        _roles,
        _workers,
        _agentInstallations,
        _runtimeStatuses,
        _managingRuntimeInstallationId,
        _teamDirectory);

    private string ConfigurationLoadingMessage => _configurationRuntime?.Stage switch
    {
        AgentRuntimeReadinessStages.Queued => "Agent runtime queued...",
        AgentRuntimeReadinessStages.StartingContainer => "Starting agent container...",
        AgentRuntimeReadinessStages.WaitingForMcpSession => "Establishing secure agent session...",
        AgentRuntimeReadinessStages.Stopping => "Cleaning up the previous runtime...",
        AgentRuntimeReadinessStages.Ready => "Loading agent configuration...",
        _ => "Preparing agent runtime..."
    };

    private IReadOnlyList<AgentChoice> AvailableAgents => _agentInstallations
        .Where(x => x.IsEnabled)
        .Select(x => new AgentChoice($"installation:{x.Id}", x.AgentName, x.AgentId, x.Id, x.GrantedCapabilities, true))
        .OrderBy(x => x.Name)
        .ToList();

    protected override async Task OnParametersSetAsync()
    {
        _loading = true;
        _errorMessage = null;

        try
        {
            _organization = await Http.GetFromJsonAsync<OrganizationResponse>($"api/organizations/{OrganizationId}");
            _employees = await Http.GetFromJsonAsync<IReadOnlyList<OrganizationUserResponse>>($"api/core/organizations/{OrganizationId}/users") ?? [];
            _roles = await Http.GetFromJsonAsync<IReadOnlyList<RoleResponse>>($"api/organizations/{OrganizationId}/roles") ?? [];
            _workers = await Http.GetFromJsonAsync<IReadOnlyList<WorkerResponse>>($"api/organizations/{OrganizationId}/workers") ?? [];
            var installationsTask = AgentApi.ListInstallationsAsync();
            var providersTask = LlmProviderApi.ListAsync();
            await Task.WhenAll(installationsTask, providersTask);
            _agentInstallations = await installationsTask;
            _providerProfiles = await providersTask;
            _hiringDashboard = await Http.GetFromJsonAsync<HiringDashboardResponse>(
                $"api/core/organizations/{OrganizationId}/hiring");
            await ReloadTeamsAsync();
            EnsureSelection();
            StartRuntimeStatusRefresh();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private string EmployeeLabel(OrganizationUserResponse employee)
    {
        var role = employee.RoleId.HasValue
            ? _roles.FirstOrDefault(x => x.Id == employee.RoleId.Value)?.Name
            : null;
        var worker = employee.WorkerId.HasValue
            ? _workers.FirstOrDefault(x => x.Id == employee.WorkerId.Value)?.Name
            : null;

        return role ?? (employee.EmployeeType == 1 ? worker ?? "Agent" : "Employee");
    }

    private async Task ReloadTeamsAsync()
    {
        _teamDirectory = await Http.GetFromJsonAsync<TeamDirectoryResponse>(
            $"api/core/organizations/{OrganizationId}/teams?includeArchived=true",
            _disposeCts.Token) ?? new TeamDirectoryResponse(Guid.Empty, false, []);
        if (_editingTeam is not null)
            _editingTeam = _teamDirectory.Teams.FirstOrDefault(x => x.Id == _editingTeam.Id);
    }

    private Task HandleTeamActionAsync(TeamUiActionRequest request)
    {
        _teamMutationError = null;
        _teamRevisionConflict = false;
        _teamRevisionReviewed = false;
        var team = request.TeamId.HasValue
            ? _teamDirectory.Teams.FirstOrDefault(x => x.Id == request.TeamId.Value)
            : null;
        switch (request.Action)
        {
            case TeamUiActionKind.Create:
                _editingTeam = null;
                _teamName = string.Empty;
                _teamDescription = null;
                _teamLeadId = _teamDirectory.CurrentOrganizationUserId != Guid.Empty
                    ? _teamDirectory.CurrentOrganizationUserId
                    : _employees.FirstOrDefault()?.Id;
                _teamInitialMemberIds.Clear();
                _teamInitialRoles.Clear();
                _teamDialogOpen = true;
                break;
            case TeamUiActionKind.Edit when team is not null:
                _editingTeam = team;
                _teamName = team.Name;
                _teamDescription = team.Description;
                _teamLeadId = team.LeadOrganizationUserId;
                _teamDialogOpen = true;
                break;
            case TeamUiActionKind.AddMember when team is not null:
                _editingTeam = team;
                _teamMemberId = null;
                _teamMemberRoleId = null;
                _teamMemberDialogOpen = true;
                break;
            case TeamUiActionKind.Archive when team is not null:
            case TeamUiActionKind.Restore when team is not null:
            case TeamUiActionKind.RemoveMember when team is not null:
                _pendingTeamAction = request;
                _teamConfirmTitle = request.Action switch
                {
                    TeamUiActionKind.Archive => $"Archive {team.Name}?",
                    TeamUiActionKind.Restore => $"Restore {team.Name}?",
                    _ => "Remove team member?"
                };
                _teamConfirmMessage = request.Action switch
                {
                    TeamUiActionKind.Archive =>
                        "The team will be hidden from active rosters and all team-scoped grants will be revoked. Restoring the team will not restore those grants.",
                    TeamUiActionKind.Restore =>
                        "The team and its membership history will return to active views. Revoked grants remain revoked.",
                    _ => "The membership will be ended and retained in team history."
                };
                _teamConfirmDialogOpen = true;
                break;
        }
        return Task.CompletedTask;
    }

    private void ToggleInitialTeamMember(Guid employeeId, bool selected)
    {
        if (selected)
            _teamInitialMemberIds.Add(employeeId);
        else
        {
            _teamInitialMemberIds.Remove(employeeId);
            _teamInitialRoles.Remove(employeeId);
        }
    }

    private Guid? InitialTeamRole(Guid employeeId) =>
        _teamInitialRoles.TryGetValue(employeeId, out var roleId) ? roleId : null;

    private void SetInitialTeamRole(Guid employeeId, Guid? roleId) =>
        _teamInitialRoles[employeeId] = roleId;

    private bool IsAgentBoundToAnotherTeam(OrganizationUserResponse employee, Guid? targetTeamId) =>
        employee.EmployeeType == (int)EmployeeType.Agent &&
        _teamDirectory.Teams.Any(team =>
            team.Id != targetTeamId &&
            team.Members.Any(membership => membership.OrganizationUserId == employee.Id));

    private async Task SaveTeamAsync()
    {
        if (string.IsNullOrWhiteSpace(_teamName) || !_teamLeadId.HasValue)
        {
            _teamMutationError = "A team name and active lead are required.";
            return;
        }
        _teamMutationBusy = true;
        _teamMutationError = null;
        try
        {
            HttpResponseMessage response;
            if (_editingTeam is null)
            {
                var members = _teamInitialMemberIds.Select(employeeId =>
                    new TeamMemberInput(employeeId, InitialTeamRole(employeeId))).ToList();
                response = await Http.PostAsJsonAsync(
                    $"api/core/organizations/{OrganizationId}/teams",
                    new CreateTeamRequest(_teamName, _teamDescription, _teamLeadId.Value, members),
                    _disposeCts.Token);
            }
            else
            {
                response = await Http.PutAsJsonAsync(
                    $"api/core/organizations/{OrganizationId}/teams/{_editingTeam.Id}",
                    new UpdateTeamRequest(
                        _teamName,
                        _teamDescription,
                        _teamLeadId.Value,
                        _editingTeam.Revision),
                    _disposeCts.Token);
            }
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _teamRevisionConflict = true;
                _teamRevisionReviewed = false;
                _teamMutationError = "The team changed while you were editing. Review the refreshed state before retrying.";
                await ReloadTeamsAsync();
                return;
            }
            await EnsureTeamMutationSucceededAsync(response);
            await ReloadTeamsAsync();
            _teamDialogOpen = false;
        }
        catch (Exception exception)
        {
            _teamMutationError = exception.Message;
        }
        finally
        {
            _teamMutationBusy = false;
        }
    }

    private async Task SaveTeamMemberAsync()
    {
        if (_editingTeam is null || !_teamMemberId.HasValue)
        {
            _teamMutationError = "Select an employee.";
            return;
        }
        _teamMutationBusy = true;
        _teamMutationError = null;
        try
        {
            var response = await Http.PutAsJsonAsync(
                $"api/core/organizations/{OrganizationId}/teams/{_editingTeam.Id}/members/{_teamMemberId.Value}",
                new UpsertTeamMembershipRequest(_teamMemberRoleId, _editingTeam.Revision),
                _disposeCts.Token);
            await EnsureTeamMutationSucceededAsync(response);
            await ReloadTeamsAsync();
            _teamMemberDialogOpen = false;
        }
        catch (Exception exception)
        {
            _teamMutationError = exception.Message;
        }
        finally
        {
            _teamMutationBusy = false;
        }
    }

    private async Task ConfirmTeamActionAsync()
    {
        if (_pendingTeamAction?.TeamId is not Guid teamId) return;
        var team = _teamDirectory.Teams.FirstOrDefault(x => x.Id == teamId);
        if (team is null) return;
        _teamMutationBusy = true;
        try
        {
            HttpResponseMessage response = _pendingTeamAction.Action switch
            {
                TeamUiActionKind.Archive => await Http.PostAsJsonAsync(
                    $"api/core/organizations/{OrganizationId}/teams/{teamId}/archive",
                    new TeamRevisionRequest(team.Revision),
                    _disposeCts.Token),
                TeamUiActionKind.Restore => await Http.PostAsJsonAsync(
                    $"api/core/organizations/{OrganizationId}/teams/{teamId}/restore",
                    new TeamRevisionRequest(team.Revision),
                    _disposeCts.Token),
                TeamUiActionKind.RemoveMember when _pendingTeamAction.OrganizationUserId.HasValue =>
                    await Http.DeleteAsync(
                        $"api/core/organizations/{OrganizationId}/teams/{teamId}/members/{_pendingTeamAction.OrganizationUserId.Value}?expectedRevision={team.Revision}",
                        _disposeCts.Token),
                _ => throw new InvalidOperationException("The team action is invalid.")
            };
            await EnsureTeamMutationSucceededAsync(response);
            await ReloadTeamsAsync();
            _teamConfirmDialogOpen = false;
            _pendingTeamAction = null;
        }
        catch (Exception exception)
        {
            _teamMutationError = exception.Message;
            _actionError = exception.Message;
        }
        finally
        {
            _teamMutationBusy = false;
        }
    }

    private static async Task EnsureTeamMutationSucceededAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
                throw new InvalidOperationException(message.GetString() ?? "The team operation failed.");
        }
        catch (JsonException)
        {
            // Preserve the status-based fallback below.
        }
        throw new InvalidOperationException($"The team operation failed ({(int)response.StatusCode}).");
    }

    private async Task DecideResourceChangeAsync(ResourceChangeRequestResponse resourceChange, string decision)
    {
        if (_resourceDecisionBusy) return;
        if (decision == ResourceChangeDecisionKinds.RequestRevision && string.IsNullOrWhiteSpace(_resourceFeedback))
        {
            _errorMessage = "Decision feedback is required when requesting a revision.";
            return;
        }

        _resourceDecisionBusy = true;
        _errorMessage = null;
        try
        {
            var payload = new ResourceChangeDecisionRequest(
                resourceChange.Id,
                decision,
                _resourceFeedback,
                $"ui:{resourceChange.Id:N}:{decision}");
            var response = await Http.PostAsJsonAsync(
                $"api/core/organizations/{OrganizationId}/hiring/resource-changes/{resourceChange.Id}/decide",
                payload,
                _disposeCts.Token);
            response.EnsureSuccessStatusCode();
            var updated = await response.Content.ReadFromJsonAsync<ResourceChangeRequestResponse>(_disposeCts.Token);
            if (updated is not null && _hiringDashboard is not null)
            {
                _hiringDashboard = _hiringDashboard with
                {
                    ResourceChanges = _hiringDashboard.ResourceChanges
                        .Select(x => x.Id == updated.Id ? updated : x)
                        .ToList()
                };
            }
            _resourceFeedback = null;
        }
        catch (Exception exception)
        {
            _errorMessage = exception.Message;
        }
        finally
        {
            _resourceDecisionBusy = false;
        }
    }

    private string ReportsTo(OrganizationUserResponse employee)
    {
        if (!employee.ReportsToOrganizationUserId.HasValue)
        {
            return "Nobody";
        }

        return _employees.FirstOrDefault(x => x.Id == employee.ReportsToOrganizationUserId.Value)?.DisplayName ?? "Unknown";
    }

    private int SubordinateCount(OrganizationUserResponse employee) =>
        _employees.Count(x => x.ReportsToOrganizationUserId == employee.Id);

    private static bool IsChattableAgent(OrganizationUserResponse employee) =>
        employee.EmployeeType == 1;

    private string RuntimeStatusLabel(OrganizationUserResponse employee) =>
        RuntimeStatus(employee)?.Stage switch
        {
            AgentRuntimeReadinessStages.Ready => "Online",
            AgentRuntimeReadinessStages.Queued => "Queued",
            AgentRuntimeReadinessStages.StartingContainer => "Starting",
            AgentRuntimeReadinessStages.WaitingForMcpSession => "Connecting",
            AgentRuntimeReadinessStages.Stopping => "Stopping",
            AgentRuntimeReadinessStages.Failed => "Failed",
            AgentRuntimeReadinessStages.Offline => "Offline",
            _ => "Checking"
        };

    private Color RuntimeStatusColor(OrganizationUserResponse employee) =>
        RuntimeStatus(employee)?.Stage switch
        {
            AgentRuntimeReadinessStages.Ready => Color.Success,
            AgentRuntimeReadinessStages.Queued or
            AgentRuntimeReadinessStages.StartingContainer or
            AgentRuntimeReadinessStages.WaitingForMcpSession or
            AgentRuntimeReadinessStages.Stopping => Color.Info,
            AgentRuntimeReadinessStages.Failed => Color.Error,
            _ => Color.Default
        };

    private AgentRuntimeReadinessResponse? RuntimeStatus(OrganizationUserResponse employee) =>
        employee.AgentInstallationId is { } installationId &&
        _runtimeStatuses.TryGetValue(installationId, out var status)
            ? status
            : null;

    private AgentInstallationResponse? Installation(OrganizationUserResponse employee) =>
        employee.AgentInstallationId is { } installationId
            ? _agentInstallations.FirstOrDefault(installation => installation.Id == installationId)
            : null;

    private bool IsManagingRuntime(OrganizationUserResponse employee) =>
        employee.AgentInstallationId == _managingRuntimeInstallationId;

    private bool CanStopRuntime(OrganizationUserResponse employee) =>
        Installation(employee)?.IsEnabled == true &&
        RuntimeStatus(employee)?.Stage is AgentRuntimeReadinessStages.Queued or
            AgentRuntimeReadinessStages.StartingContainer or
            AgentRuntimeReadinessStages.WaitingForMcpSession or
            AgentRuntimeReadinessStages.Stopping or
            AgentRuntimeReadinessStages.Ready;

    private bool CanStartRuntime(OrganizationUserResponse employee) =>
        employee.AgentInstallationId.HasValue &&
        RuntimeStatus(employee)?.Stage is null or
             AgentRuntimeReadinessStages.Offline or
             AgentRuntimeReadinessStages.Failed;

    private string RuntimeActionLabel(OrganizationUserResponse employee) =>
        RuntimeStatus(employee)?.Stage == AgentRuntimeReadinessStages.Failed
            ? "Retry"
            : RuntimeStatus(employee)?.Stage == AgentRuntimeReadinessStages.Ready
                ? Installation(employee)?.IsEnabled == false ? "Stopping" : "Running"
                : RuntimeStatus(employee)?.Stage is AgentRuntimeReadinessStages.Queued or
                    AgentRuntimeReadinessStages.StartingContainer or
                    AgentRuntimeReadinessStages.WaitingForMcpSession or
                    AgentRuntimeReadinessStages.Stopping
                    ? Installation(employee)?.IsEnabled == false ? "Stopping" : "Starting"
                    : "Start";

    private string RuntimeActionIcon(OrganizationUserResponse employee) =>
        RuntimeStatus(employee)?.Stage == AgentRuntimeReadinessStages.Failed
            ? Icons.Material.Filled.Replay
            : Icons.Material.Filled.PlayCircle;

    private async Task StartRuntimeAsync(OrganizationUserResponse employee)
    {
        if (employee.AgentInstallationId is not Guid installationId)
        {
            return;
        }

        _managingRuntimeInstallationId = installationId;
        _actionError = null;
        try
        {
            var installation = Installation(employee);
            if (installation?.IsEnabled == false)
            {
                var enabled = await AgentApi.EnableAsync(installationId, _disposeCts.Token);
                _agentInstallations = _agentInstallations
                    .Select(item => item.Id == enabled.Id ? enabled : item)
                    .ToList();
            }

            _runtimeStatuses[installationId] = await AgentApi.EnsureRuntimeAsync(installationId, _disposeCts.Token);
            StartRuntimeStatusRefresh();
        }
        catch (Exception exception)
        {
            _actionError = exception.Message;
        }
        finally
        {
            _managingRuntimeInstallationId = null;
        }
    }

    private async Task StopRuntimeAsync(OrganizationUserResponse employee)
    {
        if (employee.AgentInstallationId is not Guid installationId)
        {
            return;
        }

        _managingRuntimeInstallationId = installationId;
        _actionError = null;
        try
        {
            var disabled = await AgentApi.DisableAsync(installationId, _disposeCts.Token);
            _agentInstallations = _agentInstallations
                .Select(item => item.Id == disabled.Id ? disabled : item)
                .ToList();
            StartRuntimeStatusRefresh();
        }
        catch (Exception exception)
        {
            _actionError = exception.Message;
        }
        finally
        {
            _managingRuntimeInstallationId = null;
        }
    }

    private async Task OpenRuntimeConsoleAsync(OrganizationUserResponse employee)
    {
        _runtimeConsoleEmployee = employee;
        _runtimeConsoleOpen = true;
        await RefreshRuntimeConsoleAsync();
    }

    private async Task RefreshRuntimeConsoleAsync()
    {
        if (_runtimeConsoleEmployee?.AgentInstallationId is not Guid installationId)
        {
            return;
        }

        _loadingRuntimeConsole = true;
        _runtimeConsoleError = null;
        try
        {
            _runtimeRuns = await AgentApi.ListRunsAsync(installationId, _disposeCts.Token);
        }
        catch (Exception exception)
        {
            _runtimeConsoleError = exception.Message;
        }
        finally
        {
            _loadingRuntimeConsole = false;
        }
    }

    private void CloseRuntimeConsole() => _runtimeConsoleOpen = false;

    private static Severity RuntimeRunSeverity(string status) => status switch
    {
        "Completed" or "Running" => Severity.Success,
        "Queued" or "Starting" or "WaitingForMcpSession" => Severity.Info,
        "Cancelled" => Severity.Warning,
        _ => Severity.Error
    };

    private static string RuntimeEventLog(AgentRuntimeRunResponse run) =>
        string.Join(Environment.NewLine, run.Events.Select(runtimeEvent =>
            $"[{runtimeEvent.OccurredAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {runtimeEvent.Status}: {runtimeEvent.Reason}"));

    private void StartRuntimeStatusRefresh()
    {
        _runtimeStatusCts?.Cancel();
        _runtimeStatusCts?.Dispose();
        _runtimeStatusCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _ = RefreshRuntimeStatusesAsync(_runtimeStatusCts.Token);
    }

    private async Task RefreshRuntimeStatusesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var installationIds = _employees
                    .Where(IsChattableAgent)
                    .Select(employee => employee.AgentInstallationId)
                    .OfType<Guid>()
                    .Distinct()
                    .ToArray();
                var statusTasks = installationIds.Select(async installationId =>
                {
                    try
                    {
                        var status = await AgentApi.GetRuntimeStatusAsync(installationId, cancellationToken);
                        return (installationId, status);
                    }
                    catch when (!cancellationToken.IsCancellationRequested)
                    {
                        return (installationId, status: (AgentRuntimeReadinessResponse?)null);
                    }
                });

                foreach (var (installationId, status) in await Task.WhenAll(statusTasks))
                {
                    if (status is not null)
                    {
                        _runtimeStatuses[installationId] = status;
                    }
                }

                await InvokeAsync(StateHasChanged);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task OpenChatAsync(OrganizationUserResponse employee)
    {
        if (!IsChattableAgent(employee)) return;
        try
        {
            var response = await Http.PostAsJsonAsync(
                $"api/organizations/{OrganizationId}/communications/hub/chats",
                new CreateCommunicationChatRequest(null, null, true, true, [employee.Id]),
                _disposeCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _actionError = await response.Content.ReadAsStringAsync(_disposeCts.Token);
                return;
            }
            var chat = await response.Content.ReadFromJsonAsync<CommunicationChatResponse>(_disposeCts.Token);
            if (chat is null)
            {
                _actionError = "The conversation could not be opened.";
                return;
            }
            Navigation.NavigateTo($"/organizations/{OrganizationId}/communications/{chat.Id}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _actionError = exception.Message;
        }
    }

    private void OpenMemory(OrganizationUserResponse employee)
    {
        if (employee.EmployeeType == 1 && employee.AgentInstallationId.HasValue)
        {
            Navigation.NavigateTo($"/organizations/{OrganizationId}/employees/{employee.Id}/memory");
        }
    }

    private Task ChangeViewAsync(EmployeeViewKind view)
    {
        _activeView = view;
        return Task.CompletedTask;
    }

    private Task FocusTeamAsync(Guid teamId)
    {
        _focusedTeamId = teamId;
        _activeView = EmployeeViewKind.Teams;
        return Task.CompletedTask;
    }

    private Task SelectEmployeeAsync(Guid employeeId)
    {
        if (_employees.Any(x => x.Id == employeeId))
        {
            _selectedEmployeeId = employeeId;
        }
        return Task.CompletedTask;
    }

    private Task ChangeDegreesAsync(int degrees)
    {
        _graphDegrees = Math.Clamp(degrees, 1, 3);
        return Task.CompletedTask;
    }

    private Task ChangeFilterAsync(EmployeeDirectoryFilter filter)
    {
        _directoryFilter = filter;
        return Task.CompletedTask;
    }

    private async Task HandleEmployeeActionAsync(EmployeeActionRequest request)
    {
        var employee = _employees.FirstOrDefault(x => x.Id == request.EmployeeId);
        if (employee is null)
        {
            _actionError = "The selected employee is no longer available.";
            return;
        }

        _actionError = null;
        switch (request.Action)
        {
            case EmployeeAction.OpenChat:
                await OpenChatAsync(employee);
                break;
            case EmployeeAction.StartRuntime:
                await StartRuntimeAsync(employee);
                break;
            case EmployeeAction.StopRuntime:
                await StopRuntimeAsync(employee);
                break;
            case EmployeeAction.OpenConsole:
                await OpenRuntimeConsoleAsync(employee);
                break;
            case EmployeeAction.Configure:
                await OpenConfigurationAsync(employee);
                break;
            case EmployeeAction.OpenMemory:
                OpenMemory(employee);
                break;
            case EmployeeAction.ChangeRole:
                OpenRoleDialog(employee);
                break;
            case EmployeeAction.Fire:
                OpenFireDialog(employee);
                break;
        }
    }

    private void EnsureSelection()
    {
        if (_selectedEmployeeId.HasValue && _employees.Any(x => x.Id == _selectedEmployeeId.Value))
        {
            return;
        }

        _selectedEmployeeId = EmployeeHierarchyService.InitialFocus(PresentedEmployees);
    }

    private static string NodeInitials(OrganizationUserResponse employee)
    {
        var words = employee.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => $"{words[0][..1]}{words[^1][..1]}".ToUpperInvariant()
        };
    }

    private void OpenHireDialog()
    {
        _hireName = string.Empty;
        _hireEmail = null;
        _hireEmployeeType = 1;
        _hireAgentKey = null;
        _hireManagerId = _employees
            .OrderByDescending(employee => employee.PermissionLevel == (int)OrganizationPermissionLevel.Owner)
            .ThenBy(employee => employee.DisplayName)
            .Select(employee => (Guid?)employee.Id)
            .FirstOrDefault();
        _managedEmployeeIds.Clear();
        _hireTeamIds.Clear();
        _hireTeamRoleIds.Clear();
        _actionError = null;
        _hireDialogOpen = true;
    }

    private void CloseHireDialog() => _hireDialogOpen = false;

    private void SetManaged(Guid employeeId, bool value)
    {
        if (value)
        {
            _managedEmployeeIds.Add(employeeId);
        }
        else
        {
            _managedEmployeeIds.Remove(employeeId);
        }
    }

    private void SetHireTeam(Guid teamId, bool selected)
    {
        if (_hireEmployeeType == (int)EmployeeType.Agent)
            _hireTeamIds.Clear();
        if (selected)
            _hireTeamIds.Add(teamId);
        else
        {
            _hireTeamIds.Remove(teamId);
            _hireTeamRoleIds.Remove(teamId);
        }
    }

    private Guid? HireTeamRole(Guid teamId) =>
        _hireTeamRoleIds.TryGetValue(teamId, out var roleId) ? roleId : null;

    private void SetHireTeamRole(Guid teamId, Guid? roleId) =>
        _hireTeamRoleIds[teamId] = roleId;

    private async Task HireAsync()
    {
        _actionError = null;
        if (string.IsNullOrWhiteSpace(_hireName))
        {
            _actionError = "Name is required.";
            return;
        }

        if (_hireManagerId.HasValue && _managedEmployeeIds.Contains(_hireManagerId.Value))
        {
            _actionError = "The new employee cannot both manage and report to the same person.";
            return;
        }

        if (_hireEmployeeType == 1 && string.IsNullOrWhiteSpace(_hireAgentKey))
        {
            _actionError = "Select an available agent.";
            return;
        }
        if (_hireEmployeeType == 1 && !_hireManagerId.HasValue)
        {
            _actionError = "Select the employee who will manage this agent.";
            return;
        }
        if (_hireEmployeeType == (int)EmployeeType.Agent && _hireTeamIds.Count > 1)
        {
            _actionError = "Select at most one team for an AI employee instance.";
            return;
        }

        _saving = true;
        try
        {
            Guid? workerId = null;
            if (_hireEmployeeType == 1)
            {
                workerId = await ResolveAgentWorkerAsync();
                if (!workerId.HasValue)
                {
                    return;
                }
            }

            var request = new CreateOrganizationUserRequest(
                _hireName, _hireEmail, PermissionLevel: 0, EmployeeType: _hireEmployeeType,
                WorkerId: workerId,
                ReportsToOrganizationUserId: _hireManagerId,
                ManagedOrganizationUserIds: _managedEmployeeIds.ToArray(),
                AgentInstallationId: _hireEmployeeType == (int)EmployeeType.Agent
                    ? AvailableAgents.First(x => x.Key == _hireAgentKey).InstallationId
                    : null);
            var response = await Http.PostAsJsonAsync($"api/core/organizations/{OrganizationId}/users", request);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await response.Content.ReadFromJsonAsync<CoreActionResponse>();
                _actionError = failure?.Message ?? "The employee could not be hired.";
                return;
            }

            var hiredEmployee = await response.Content.ReadFromJsonAsync<OrganizationUserResponse>();
            if (hiredEmployee is null)
            {
                _actionError = "The employee was hired, but the response was empty.";
                return;
            }

            foreach (var teamId in _hireTeamIds.ToList())
            {
                var team = _teamDirectory.Teams.First(x => x.Id == teamId);
                var membershipResponse = await Http.PutAsJsonAsync(
                    $"api/core/organizations/{OrganizationId}/teams/{teamId}/members/{hiredEmployee.Id}",
                    new UpsertTeamMembershipRequest(HireTeamRole(teamId), team.Revision),
                    _disposeCts.Token);
                await EnsureTeamMutationSucceededAsync(membershipResponse);
                var updated = await membershipResponse.Content.ReadFromJsonAsync<TeamDetailResponse>(
                    _disposeCts.Token);
                if (updated is not null)
                {
                    _teamDirectory = _teamDirectory with
                    {
                        Teams = _teamDirectory.Teams
                            .Select(item => item.Id == teamId ? updated.Team : item)
                            .ToList()
                    };
                }
            }

            _hireDialogOpen = false;
            if (_hireEmployeeType == 1 && hiredEmployee.InitialConversationId.HasValue)
            {
                Navigation.NavigateTo(
                    $"/organizations/{OrganizationId}/communications/{hiredEmployee.InitialConversationId.Value:D}");
                return;
            }

            await LoadEmployeesAsync();
        }
        catch (Exception ex)
        {
            _actionError = ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    private void OpenFireDialog(OrganizationUserResponse employee)
    {
        if (IsSelf(employee))
        {
            return;
        }

        _employeeToFire = employee;
        _actionError = null;
        _fireDialogOpen = true;
    }

    private void CloseFireDialog() => _fireDialogOpen = false;

    private void OpenRoleDialog(OrganizationUserResponse employee)
    {
        _roleEmployee = employee;
        _selectedRoleId = employee.RoleId;
        _actionError = null;
        _roleDialogOpen = true;
    }

    private void CloseRoleDialog() => _roleDialogOpen = false;

    private async Task SaveRoleAsync()
    {
        if (_roleEmployee is null) return;
        _saving = true;
        _actionError = null;
        try
        {
            var response = await Http.PutAsJsonAsync(
                $"api/core/organizations/{OrganizationId}/users/{_roleEmployee.Id}/role",
                new UpdateOrganizationUserRoleRequest(_selectedRoleId));
            if (!response.IsSuccessStatusCode)
            {
                var failure = await response.Content.ReadFromJsonAsync<CoreActionResponse>();
                _actionError = failure?.Message ?? "The company role could not be changed.";
                return;
            }
            _roleDialogOpen = false;
            await LoadEmployeesAsync();
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task FireAsync()
    {
        if (_employeeToFire is null || IsSelf(_employeeToFire))
        {
            return;
        }

        _saving = true;
        _actionError = null;
        try
        {
            var response = await Http.DeleteAsync($"api/core/organizations/{OrganizationId}/users/{_employeeToFire.Id}");
            if (!response.IsSuccessStatusCode)
            {
                var failure = await response.Content.ReadFromJsonAsync<CoreActionResponse>();
                _actionError = failure?.Message ?? "The employee could not be fired.";
                return;
            }

            _fireDialogOpen = false;
            _employeeToFire = null;
            await LoadEmployeesAsync();
        }
        catch (Exception ex)
        {
            _actionError = ex.Message;
        }
        finally
        {
            _saving = false;
        }
    }

    private static bool IsSelf(OrganizationUserResponse employee) =>
        employee.ApplicationUserId.HasValue;

    private async Task LoadEmployeesAsync()
    {
        _employees = await Http.GetFromJsonAsync<IReadOnlyList<OrganizationUserResponse>>($"api/core/organizations/{OrganizationId}/users") ?? [];
        EnsureSelection();
        StartRuntimeStatusRefresh();
    }

    private async Task OpenConfigurationAsync(OrganizationUserResponse employee)
    {
        if (employee.AgentInstallationId is not Guid installationId)
        {
            _actionError = "This agent employee is not linked to an installation.";
            return;
        }

        _configurationEmployee = employee;
        _configurationDialogOpen = true;
        _loadingConfiguration = true;
        _configurationError = null;
        _configurationMessage = null;
        _configurationRuntime = null;
        _configurationValues.Clear();
        _configurationCts?.Cancel();
        _configurationCts?.Dispose();
        _configurationCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        _configurationCts.CancelAfter(TimeSpan.FromSeconds(90));
        var cancellationToken = _configurationCts.Token;
        try
        {
            _configurationRuntime = await AgentApi.EnsureRuntimeAsync(installationId, cancellationToken);
            while (!_configurationRuntime.IsReady)
            {
                if (_configurationRuntime.IsTerminal)
                {
                    throw new InvalidOperationException(
                        _configurationRuntime.Reason ?? "The agent runtime could not be started.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                _configurationRuntime = await AgentApi.GetRuntimeStatusAsync(installationId, cancellationToken);
                StateHasChanged();
            }

            _configurationSchema = await AgentApi.GetConfigurationAsync(
                installationId.ToString(),
                cancellationToken);
            foreach (var field in _configurationSchema.Fields)
            {
                _configurationValues[field.Key] = _configurationSchema.Settings.TryGetValue(field.Key, out var value)
                    ? field.Type switch
                    {
                        AgentConfigurationFieldTypes.Boolean => value.ValueKind == JsonValueKind.True,
                        AgentConfigurationFieldTypes.Number when value.TryGetDecimal(out var number) => number,
                        _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null
                    }
                    : null;
            }
            await LoadConfiguredProviderModelsAsync(_configurationSchema, cancellationToken);
        }
        catch (OperationCanceledException) when (_configurationCts.IsCancellationRequested)
        {
            if (_configurationDialogOpen && !_disposeCts.IsCancellationRequested)
            {
                _configurationError = "The agent runtime did not become ready in time. Try again or review its run history.";
            }
            _configurationSchema = null;
        }
        catch (Exception exception)
        {
            _configurationError = exception.Message;
            _configurationSchema = null;
        }
        finally
        {
            _loadingConfiguration = false;
        }
    }

    private async Task SaveConfigurationAsync()
    {
        if (_configurationEmployee?.AgentInstallationId is not Guid installationId || _configurationSchema is null) return;
        _savingConfiguration = true;
        _configurationError = null;
        _configurationMessage = null;
        try
        {
            var settings = _configurationSchema.Fields.ToDictionary(
                field => field.Key,
                field => JsonSerializer.SerializeToElement(_configurationValues.GetValueOrDefault(field.Key), SerializerOptions),
                StringComparer.Ordinal);
            var result = await AgentApi.UpdateConfigurationAsync(
                installationId.ToString(),
                new UpdateAgentConfigurationRequest(settings)
                {
                    SchemaVersion = _configurationSchema.SchemaVersion
                });
            if (!result.Succeeded) throw new InvalidOperationException(result.Message ?? "The agent rejected its configuration.");
            _configurationMessage = result.Message ?? "Agent instance configuration saved.";
        }
        catch (Exception exception)
        {
            _configurationError = exception.Message;
        }
        finally
        {
            _savingConfiguration = false;
        }
    }

    private void CloseConfiguration()
    {
        _configurationDialogOpen = false;
        _configurationCts?.Cancel();
    }
    private string ConfigurationString(string key) => _configurationValues.GetValueOrDefault(key)?.ToString() ?? string.Empty;
    private bool ConfigurationBoolean(string key) => _configurationValues.GetValueOrDefault(key) is true;
    private decimal? ConfigurationNumber(string key) => _configurationValues.GetValueOrDefault(key) as decimal?;
    private void SetConfigurationValue(string key, object? value) => _configurationValues[key] = value;

    private async Task SetProviderValueAsync(string key, string value)
    {
        _configurationValues[key] = value;
        if (_configurationSchema is null)
        {
            return;
        }

        var provider = FindProvider(value);
        if (provider is not null)
        {
            await LoadProviderModelsAsync(provider.Id, _disposeCts.Token);
        }

        foreach (var modelField in _configurationSchema.Fields.Where(field =>
            field.Type == AgentConfigurationFieldTypes.LlmModel &&
            string.Equals(ConfigurationProviderFieldKey(field), key, StringComparison.Ordinal)))
        {
            var models = provider is null ? [] : ModelOptions(modelField);
            _configurationValues[modelField.Key] = provider is null
                ? string.Empty
                : models.Contains(provider.DefaultChatModel, StringComparer.Ordinal)
                    ? provider.DefaultChatModel
                    : models.FirstOrDefault() ?? string.Empty;
        }
    }

    private bool IsModelPickerDisabled(AgentConfigurationField field) =>
        ConfigurationProvider(field) is not { } provider ||
        _loadingProviderModels.Contains(provider.Id);

    private string ModelPlaceholder(AgentConfigurationField field) =>
        ConfigurationProvider(field) is not { } provider
            ? "Select a provider first"
            : _loadingProviderModels.Contains(provider.Id)
                ? "Loading models..."
                : "Select a model";

    private IReadOnlyList<string> ModelOptions(AgentConfigurationField field)
    {
        var provider = ConfigurationProvider(field);
        if (provider is null)
        {
            return [];
        }

        var models = _providerModels.GetValueOrDefault(provider.Id) ?? [];
        return models
            .Append(provider.DefaultChatModel)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private bool SelectedModelMissing(AgentConfigurationField field)
    {
        var selected = ConfigurationString(field.Key);
        return !string.IsNullOrWhiteSpace(selected) &&
            !ModelOptions(field).Contains(selected, StringComparer.Ordinal);
    }

    private LlmProviderProfileResponse? ConfigurationProvider(AgentConfigurationField field) =>
        FindProvider(ConfigurationString(ConfigurationProviderFieldKey(field)));

    private string ConfigurationProviderFieldKey(AgentConfigurationField modelField) =>
        !string.IsNullOrWhiteSpace(modelField.DependsOnFieldKey)
            ? modelField.DependsOnFieldKey
            : _configurationSchema?.Fields.FirstOrDefault(field =>
                field.Type == AgentConfigurationFieldTypes.LlmProvider)?.Key ?? string.Empty;

    private LlmProviderProfileResponse? FindProvider(string providerId) =>
        Guid.TryParse(providerId, out var id)
            ? _providerProfiles.FirstOrDefault(provider => provider.Id == id && provider.IsEnabled)
            : null;

    private async Task LoadConfiguredProviderModelsAsync(
        AgentConfigurationSchemaResponse schema,
        CancellationToken cancellationToken)
    {
        var providerIds = schema.Fields
            .Where(field => field.Type == AgentConfigurationFieldTypes.LlmProvider)
            .Select(field => ConfigurationString(field.Key))
            .Where(value => Guid.TryParse(value, out _))
            .Select(Guid.Parse)
            .Distinct()
            .ToList();

        await Task.WhenAll(providerIds.Select(providerId =>
            LoadProviderModelsAsync(providerId, cancellationToken)));

        foreach (var modelField in schema.Fields.Where(field =>
                     field.Type == AgentConfigurationFieldTypes.LlmModel &&
                     string.IsNullOrWhiteSpace(ConfigurationString(field.Key))))
        {
            var provider = ConfigurationProvider(modelField);
            if (provider is null)
            {
                continue;
            }

            var models = ModelOptions(modelField);
            _configurationValues[modelField.Key] =
                models.Contains(provider.DefaultChatModel, StringComparer.Ordinal)
                    ? provider.DefaultChatModel
                    : models.FirstOrDefault() ?? string.Empty;
        }
    }

    private async Task LoadProviderModelsAsync(Guid providerId, CancellationToken cancellationToken)
    {
        if ((_providerModels.ContainsKey(providerId) && !_providerModelErrors.ContainsKey(providerId)) ||
            !_loadingProviderModels.Add(providerId))
        {
            return;
        }

        _providerModelErrors.Remove(providerId);
        try
        {
            var result = await LlmProviderApi.GetModelCatalogAsync(providerId, cancellationToken);
            if (!result.Succeeded)
            {
                _providerModelErrors[providerId] =
                    result.Message ?? "Models could not be loaded from this provider.";
                _providerModels[providerId] = [];
                return;
            }

            _providerModels[providerId] = result.Models
                .Select(model => model.Id)
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _providerModelErrors[providerId] = exception.Message;
            _providerModels[providerId] = [];
        }
        finally
        {
            _loadingProviderModels.Remove(providerId);
        }
    }

    private string? ModelCatalogError(AgentConfigurationField providerField)
    {
        var provider = FindProvider(ConfigurationString(providerField.Key));
        return provider is not null &&
               _providerModelErrors.TryGetValue(provider.Id, out var error)
            ? error
            : null;
    }

    private async Task<Guid?> ResolveAgentWorkerAsync()
    {
        var choice = AvailableAgents.FirstOrDefault(x => x.Key == _hireAgentKey);
        if (choice is null)
        {
            _actionError = "The selected agent is no longer available.";
            return null;
        }

        var existing = _workers.FirstOrDefault(x =>
            x.IsEnabled &&
            (string.Equals(x.Name, choice.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(x.Name, WithoutBrandPrefix(choice.Name), StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            return existing.Id;
        }

        var endpointConfiguration = JsonSerializer.Serialize(new
        {
            agentId = choice.AgentId,
            installationId = choice.InstallationId
        });
        var createWorker = new CreateWorkerRequest(
            choice.Name,
            $"Employee backed by the installed agent {choice.AgentId}.",
            choice.IsInstallation ? 1 : 0,
            choice.IsInstallation ? 1 : 0,
            JsonSerializer.Serialize(choice.Capabilities),
            null,
            endpointConfiguration,
            true,
            false);
        var response = await Http.PostAsJsonAsync($"api/organizations/{OrganizationId}/workers", createWorker);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await response.Content.ReadFromJsonAsync<CoreActionResponse>();
            _actionError = failure?.Message ?? "The selected agent could not be prepared for hiring.";
            return null;
        }

        var worker = await response.Content.ReadFromJsonAsync<WorkerResponse>();
        if (worker is null)
        {
            _actionError = "The selected agent could not be prepared for hiring.";
            return null;
        }

        _workers = _workers.Append(worker).ToList();
        return worker.Id;
    }

    private static string WithoutBrandPrefix(string name) =>
        name.StartsWith("C-Sweet ", StringComparison.OrdinalIgnoreCase)
            ? name[8..]
            : name.StartsWith("CSweet ", StringComparison.OrdinalIgnoreCase)
                ? name[7..]
                : name;

    private sealed record AgentChoice(
        string Key,
        string Name,
        string AgentId,
        Guid? InstallationId,
        IReadOnlyList<string> Capabilities,
        bool IsInstallation);

    public void Dispose()
    {
        _configurationCts?.Cancel();
        _configurationCts?.Dispose();
        _runtimeStatusCts?.Cancel();
        _runtimeStatusCts?.Dispose();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}
