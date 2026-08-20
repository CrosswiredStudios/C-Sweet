using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.Core;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

public sealed class AgentHireOperationState(HttpClient http, AppRealtimeState realtime) : IDisposable
{
    private readonly Dictionary<Guid, PendingStart> _pendingStarts = [];
    private readonly HashSet<Guid> _autoDismissScheduled = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _pollTask;
    private bool _initialized;

    public IReadOnlyList<AgentHireOperationResponse> Operations { get; private set; } = [];
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        _initialized = true;
        realtime.EventReceived += OnRealtimeEvent;
        realtime.Reconnected += OnReconnected;
        await RefreshAsync(cancellationToken);
    }

    public void Start(
        Guid organizationId,
        Guid workflowId,
        string agentName,
        string employeeDisplayName,
        ConfirmHiringWorkflowRequest request)
    {
        var pending = new PendingStart(organizationId, workflowId, agentName, employeeDisplayName, request);
        _pendingStarts[workflowId] = pending;
        Merge(Starting(pending));
        _ = StartCoreAsync(pending, _disposeCts.Token);
    }

    public async Task RetryAsync(AgentHireOperationResponse operation)
    {
        if (_pendingStarts.TryGetValue(operation.WorkflowId, out var pending))
        {
            Merge(Starting(pending));
            await StartCoreAsync(pending, _disposeCts.Token);
            return;
        }

        var response = await http.PostAsync(
            $"api/core/hiring/operations/{operation.Id:D}/retry", null, _disposeCts.Token);
        await MergeResponseAsync(response, "The agent hire could not be retried.");
    }

    public async Task DismissAsync(AgentHireOperationResponse operation)
    {
        if (_pendingStarts.Remove(operation.WorkflowId))
        {
            Remove(operation.Id);
            return;
        }
        var response = await http.PostAsync(
            $"api/core/hiring/operations/{operation.Id:D}/dismiss", null, _disposeCts.Token);
        if (response.IsSuccessStatusCode) Remove(operation.Id);
    }

    public async Task CancelInterruptedAsync(AgentHireOperationResponse operation)
    {
        var response = await http.PostAsync(
            $"api/core/organizations/{operation.OrganizationId:D}/hiring/workflows/{operation.WorkflowId:D}/cancel-preview",
            null,
            _disposeCts.Token);
        if (response.IsSuccessStatusCode) Remove(operation.Id);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var serverOperations = await http.GetFromJsonAsync<IReadOnlyList<AgentHireOperationResponse>>(
                "api/core/hiring/operations", cancellationToken) ?? [];
            var optimistic = Operations.Where(x => _pendingStarts.ContainsKey(x.WorkflowId)).ToList();
            Operations = serverOperations.Concat(optimistic)
                .GroupBy(x => x.WorkflowId)
                .Select(x => x.OrderByDescending(y => y.UpdatedAt).First())
                .OrderBy(x => x.UpdatedAt)
                .ToList();
            Changed?.Invoke();
            StartPollingIfNeeded();
            ScheduleSuccessDismissals();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task StartCoreAsync(PendingStart pending, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.PostAsJsonAsync(
                $"api/core/organizations/{pending.OrganizationId:D}/hiring/workflows/{pending.WorkflowId:D}/confirm",
                pending.Request,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ErrorMessageAsync(response, "The agent hire could not be started."));
            var operation = await response.Content.ReadFromJsonAsync<AgentHireOperationResponse>(cancellationToken)
                ?? throw new InvalidOperationException("The agent hire response was empty.");
            _pendingStarts.Remove(pending.WorkflowId);
            Operations = Operations.Where(x => x.WorkflowId != pending.WorkflowId).ToList();
            Merge(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Merge(Starting(pending) with
            {
                Status = AgentHireOperationStatuses.Failed,
                Phase = "Hire could not start",
                Detail = exception.Message,
                Error = exception.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private async Task MergeResponseAsync(HttpResponseMessage response, string fallback)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await ErrorMessageAsync(response, fallback));
        var operation = await response.Content.ReadFromJsonAsync<AgentHireOperationResponse>(_disposeCts.Token)
            ?? throw new InvalidOperationException(fallback);
        Merge(operation);
    }

    private void OnRealtimeEvent(AppRealtimeEventEnvelope envelope)
    {
        if (envelope.EventType == AppRealtimeEvents.AgentHireOperationChanged)
            _ = RefreshAsync(_disposeCts.Token);
    }

    private void OnReconnected() => _ = RefreshAsync(_disposeCts.Token);

    private void StartPollingIfNeeded()
    {
        if (!Operations.Any(x => AgentHireOperationStatuses.IsActive(x.Status)) ||
            _pollTask is { IsCompleted: false }) return;
        _pollTask = PollAsync(_disposeCts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!cancellationToken.IsCancellationRequested &&
               Operations.Any(x => AgentHireOperationStatuses.IsActive(x.Status)))
        {
            if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            await RefreshAsync(cancellationToken);
        }
    }

    private void ScheduleSuccessDismissals()
    {
        foreach (var operation in Operations.Where(x => x.Status == AgentHireOperationStatuses.Succeeded))
        {
            if (_autoDismissScheduled.Add(operation.Id))
                _ = AutoDismissAsync(operation, _disposeCts.Token);
        }
    }

    private async Task AutoDismissAsync(AgentHireOperationResponse operation, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        await DismissAsync(operation);
    }

    private void Merge(AgentHireOperationResponse operation)
    {
        Operations = Operations.Where(x => x.Id != operation.Id && x.WorkflowId != operation.WorkflowId)
            .Append(operation).OrderBy(x => x.UpdatedAt).ToList();
        Changed?.Invoke();
        StartPollingIfNeeded();
        ScheduleSuccessDismissals();
    }

    private void Remove(Guid operationId)
    {
        Operations = Operations.Where(x => x.Id != operationId).ToList();
        Changed?.Invoke();
    }

    private static AgentHireOperationResponse Starting(PendingStart pending) => new(
        pending.WorkflowId,
        pending.WorkflowId,
        pending.OrganizationId,
        null,
        pending.AgentName,
        pending.EmployeeDisplayName,
        AgentHireOperationStatuses.Starting,
        "Starting hire",
        $"Preparing {pending.EmployeeDisplayName}…",
        0,
        0,
        null,
        null,
        false,
        null,
        null,
        DateTimeOffset.UtcNow);

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>();
            return error?.Message ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        realtime.EventReceived -= OnRealtimeEvent;
        realtime.Reconnected -= OnReconnected;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _refreshLock.Dispose();
    }

    private sealed record PendingStart(
        Guid OrganizationId,
        Guid WorkflowId,
        string AgentName,
        string EmployeeDisplayName,
        ConfirmHiringWorkflowRequest Request);

    private sealed record ApiError(string? Error, string? Message);
}
