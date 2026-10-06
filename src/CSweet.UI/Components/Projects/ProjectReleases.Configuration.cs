using System.Net.Http.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UI.Components.Projects;

public partial class ProjectReleases
{
    private bool _editing;
    private WorkDeliveryPlanResponse? _editingPlan;
    private string _releaseName = "";
    private Guid? _managerId, _qaId, _technicalId;
    private readonly List<EpicInput> _epicInputs = [];
    private readonly List<BranchInput> _branchInputs = [];
    private readonly List<WorkBoardDetailResponse> _boards = [];
    private SourceControlRepositoryOptionResponse[] _repositories = [];
    private Guid? _bindingStory, _bindingRepository;
    private WorkDeliveryPlanResponse? _taskPlan;
    private readonly List<TaskInput> _taskInputs = [];
    private readonly Dictionary<Guid, ResolutionInput> _resolutions = [];
    private readonly Dictionary<string, string> _operationKeys = [];
    private string OperationKey(string action, Guid id, long revision)
    {
        var identity = $"{action}:{id}:{revision}";
        if (!_operationKeys.TryGetValue(identity, out var key)) _operationKeys[identity] = key = Guid.NewGuid().ToString("N");
        return key;
    }
    private static Guid[] ParseIds(string value) => value.Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Guid.Parse).Distinct().ToArray();
    private ResolutionInput Resolution(Guid finding)
    { if (!_resolutions.TryGetValue(finding, out var input)) _resolutions[finding] = input = new(); return input; }
    private async Task OpenConfigurationAsync(WorkDeliveryPlanResponse? plan)
    {
        _busy = true; _error = null;
        try
        {
            var directory = await Http.GetFromJsonAsync<WorkBoardDirectoryResponse>($"api/organizations/{OrganizationId:D}/work/boards?workstreamId={ProjectId:D}");
            _boards.Clear(); _epicInputs.Clear(); _branchInputs.Clear();
            foreach (var entry in directory?.Boards.Where(x => x.WorkstreamId == ProjectId) ?? [])
            {
                var board = await Http.GetFromJsonAsync<WorkBoardDetailResponse>($"api/organizations/{OrganizationId:D}/work/boards/{entry.Id:D}");
                if (board is null) continue;
                _boards.Add(board);
                foreach (var epic in board.Items.Where(x => x.Kind == "Epic" && x.ExecutionMode == WorkItemExecutionModes.Container))
                    _epicInputs.Add(new() { Id = epic.Id, BoardId = entry.Id, BoardName = entry.Name, Title = epic.Title,
                        Selected = plan?.EpicItemIds.Contains(epic.Id) ?? false,
                        Isolated = plan?.Branches.Any(x => x.Scope == "Epic" && x.ItemId == epic.Id) ?? false });
            }
            using var repositoryResponse = await Http.GetAsync($"api/organizations/{OrganizationId:D}/source-control/repositories");
            _repositories = repositoryResponse.IsSuccessStatusCode ? await repositoryResponse.Content.ReadFromJsonAsync<SourceControlRepositoryOptionResponse[]>() ?? [] : [];
            if (plan is not null) _branchInputs.AddRange(plan.Branches.Select(x => new BranchInput(x)));
            else
            {
                var releaseName = $"codex/release/{ProjectId:N}/{Guid.NewGuid():N}";
                foreach (var board in _boards)
                foreach (var story in board.Items.Where(x => x.Kind == "Story" && x.ExecutionMode == WorkItemExecutionModes.Container))
                foreach (var repo in board.Items.Where(x => x.ParentItemId == story.Id && x.Delivery is { DeliveryKind: "Code" })
                    .Select(x => x.Delivery!.RepositoryId).Distinct())
                {
                    var option = _repositories.Single(x => x.Id == repo);
                    _branchInputs.Add(new(new(repo, "Story", story.Id, $"codex/story/{story.Id:N}", releaseName)));
                    if (!_branchInputs.Any(x => x.RepositoryId == repo && x.Scope == "Release"))
                        _branchInputs.Add(new(new(repo, "Release", null, releaseName, option.DefaultBranch)));
                }
            }
            _editingPlan = plan; _releaseName = plan?.Name ?? ""; _managerId = plan?.ManagerOrganizationUserId ?? _currentUser;
            _qaId = plan?.Scopes.SelectMany(x => x.Stages).FirstOrDefault(x => x.StageKey == "quality")?.OrganizationUserId;
            _technicalId = plan?.Scopes.SelectMany(x => x.Stages).FirstOrDefault(x => x.StageKey == "technical-review")?.OrganizationUserId;
            _editing = true;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidOperationException) { _error = error.Message; }
        finally { _busy = false; }
    }
    private WorkStageAssignment Principal(string stage, Guid personId)
    {
        var person = _people.Single(x => x.Id == personId);
        return new(stage, person.AgentInstallationId.HasValue ? "AgentInstallation" : "Human", person.Id, person.AgentInstallationId);
    }
    private void AddBinding()
    {
        var repository = _repositories.Single(x => x.Id == _bindingRepository);
        var release = _branchInputs.FirstOrDefault(x => x.RepositoryId == repository.Id && x.Scope == "Release");
        if (release is null)
        {
            release = new(new(repository.Id, "Release", null, $"codex/release/{ProjectId:N}/{Guid.NewGuid():N}", repository.DefaultBranch));
            _branchInputs.Add(release);
        }
        if (!_branchInputs.Any(x => x.Scope == "Story" && x.RepositoryId == repository.Id && x.Binding().ItemId == _bindingStory))
            _branchInputs.Add(new(new(repository.Id, "Story", _bindingStory, $"codex/story/{_bindingStory:N}", release.Source)));
    }
    private async Task OpenTaskAssignmentsAsync(WorkDeliveryPlanResponse plan)
    {
        await OpenConfigurationAsync(plan); _editing = false; _taskPlan = plan; _taskInputs.Clear();
        foreach (var board in _boards)
        foreach(var task in board.Items.Where(x => x.ExecutionMode == WorkItemExecutionModes.Executable && x.Status is not ("Done" or "Completed") &&
            plan.Scopes.Any(s => s.Scope == "Story" && s.ChildIds.Contains(x.Id))))
            _taskInputs.Add(new() { Item = task, Profile = board.Board.ProfileKey, AuthorId = task.StageAssignments.FirstOrDefault(x => x.StageKey is "development" or "specialist-execution")?.OrganizationUserId,
                Kind = task.Delivery?.DeliveryKind ?? task.Planning?.DeliveryKind ?? "Code", RepositoryId = task.Delivery?.RepositoryId });
    }
    private async Task FinalizeTaskAsync(TaskInput input)
    {
        var plan = _taskPlan!; var item = input.Item; var planning = item.Planning!;
        var code = input.Kind == "Code";
        var authorStage = input.Profile is WorkBoardProfileKeys.SoftwareDeliveryV2 or WorkBoardProfileKeys.ProjectDeliveryV2 ? "development" : "specialist-execution";
        var stages = new List<WorkStageAssignment> { Principal(authorStage, input.AuthorId!.Value), Principal("quality", _qaId!.Value) };
        if(code) { stages.Add(Principal("technical-review", _technicalId!.Value)); stages.Add(new("task-integration", "PlatformAction", PlatformAction: HierarchicalWorkflows.TaskIntegrationAction)); }
        var delivery = new WorkItemDeliverySpecification(code ? input.RepositoryId!.Value : Guid.Empty, planning.Requirements, planning.AcceptanceCriteria, planning.Constraints)
        { DeliveryPlanId = plan.Id, DeliveryKind = input.Kind, DependencyItemIds = planning.DependencyItemIds,
            BaseBranch = code ? plan.Branches.Single(x => x.Scope == "Story" && x.ItemId == item.ParentItemId && x.RepositoryId == input.RepositoryId).SourceBranch : "" };
        await MutateAsync("tasks/finalize", new FinalizeWorkItemDeliveryRequest(item.BoardId, item.Id, delivery, input.AuthorId.Value, stages,
            item.Revision, OperationKey("finalize", item.Id, item.Revision)));
        if(_error is null) await OpenTaskAssignmentsAsync(_plans.Single(x => x.Id == plan.Id));
    }
    private async Task SaveConfigurationAsync()
    {
        try
        {
            var epics = _epicInputs.Where(x => x.Selected).ToArray();
            var assignments = new List<WorkDeliveryScopeAssignment>();
            var bindings = _branchInputs.Select(x => x.Binding()).ToList();
            var stories = _boards.SelectMany(x => x.Items).Where(x => x.Kind == "Story" && epics.Any(e => e.Id == x.ParentItemId)).ToArray();
            bindings.RemoveAll(x => x.Scope == "Story" && !stories.Any(s => s.Id == x.ItemId) || x.Scope == "Epic");
            foreach (var epic in epics)
            {
                var epicStages = new List<WorkStageAssignment> { Principal("technical-review", _technicalId!.Value) };
                if (epic.Isolated)
                {
                    epicStages.Add(Principal("quality", _qaId!.Value));
                    foreach (var release in bindings.Where(x => x.Scope == "Release").ToArray())
                    {
                        var ownedStories = stories.Where(x => x.ParentItemId == epic.Id).Select(x => x.Id).ToHashSet();
                        if (!bindings.Any(x => x.Scope == "Story" && ownedStories.Contains(x.ItemId!.Value) && x.RepositoryId == release.RepositoryId)) continue;
                        var source = $"codex/epic/{epic.Id:N}";
                        bindings.Add(new(release.RepositoryId, "Epic", epic.Id, source, release.SourceBranch));
                        for (var index = 0; index < bindings.Count; index++)
                            if (bindings[index].Scope == "Story" && ownedStories.Contains(bindings[index].ItemId!.Value) && bindings[index].RepositoryId == release.RepositoryId)
                                bindings[index] = bindings[index] with { TargetBranch = source };
                    }
                }
                assignments.Add(new("Epic", epic.Id, epic.BoardId, epicStages));
            }
            foreach (var story in stories) assignments.Add(new("Story", story.Id, story.BoardId, [Principal("quality", _qaId!.Value)]));
            var releaseStages = new List<WorkStageAssignment> { Principal("quality", _qaId!.Value), Principal("technical-review", _technicalId!.Value) };
            if (_editingPlan?.Scopes.Single(x => x.Scope == "Release").Stages.SingleOrDefault(x => x.StageKey == "build-readiness") is { } buildStage)
                releaseStages.Insert(0, buildStage);
            assignments.Add(new("Release", null, epics.First().BoardId, releaseStages));
            var request = new ConfigureWorkDeliveryPlanRequest(ProjectId, _releaseName, _managerId!.Value, epics.Select(x => x.Id).ToArray(), bindings, assignments,
                OperationKey("configure", _editingPlan?.Id ?? ProjectId, _editingPlan?.Revision ?? 0))
            { PlanId = _editingPlan?.Id, ExpectedRevision = _editingPlan?.Revision ?? 0, ReleasePlanning = _editingPlan?.Scopes.Single(x => x.Scope == "Release").Planning };
            await MutateAsync("configure", request);
            if (_error is null) _editing = false;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException) { _error = error.Message; }
    }
    private sealed class EpicInput { public Guid Id, BoardId; public string Title = "", BoardName = ""; public bool Selected { get; set; } public bool Isolated { get; set; } }
    private sealed class ResolutionInput { public string Tasks { get; set; } = ""; public string Evidence { get; set; } = ""; }
    private sealed class TaskInput { public WorkBoardItemResponse Item = null!; public string Profile = ""; public string Kind { get; set; } = "Code"; public Guid? AuthorId { get; set; } public Guid? RepositoryId { get; set; } }
    private sealed class BranchInput(WorkDeliveryBranchBinding binding)
    {
        public Guid RepositoryId => binding.RepositoryId;
        public string Scope => binding.Scope;
        public string Source { get; set; } = binding.SourceBranch;
        public string Target { get; set; } = binding.TargetBranch;
        public WorkDeliveryBranchBinding Binding() => binding with { SourceBranch = Source, TargetBranch = Target };
    }
}
