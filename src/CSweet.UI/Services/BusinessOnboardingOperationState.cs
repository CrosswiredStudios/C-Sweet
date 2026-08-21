using System.Net.Http.Json;
using System.Text.Json;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Realtime;

namespace CSweet.UI.Services;

public sealed class BusinessOnboardingOperationState(
    HttpClient http,
    AppRealtimeState realtime,
    IBusinessContext businessContext) : IDisposable
{
    private readonly Dictionary<Guid, PendingStart> _pendingStarts = [];
    private readonly HashSet<Guid> _autoDismissScheduled = [];
    private readonly HashSet<Guid> _businessContextRefreshed = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _pollTask;
    private bool _initialized;

    public IReadOnlyList<BusinessOnboardingOperationResponse> Operations { get; private set; } = [];
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        _initialized = true;
        realtime.EventReceived += OnRealtimeEvent;
        realtime.Reconnected += OnReconnected;
        await RefreshAsync(cancellationToken);
    }

    public void Start(StartBusinessOnboardingRequest request)
    {
        var pending = new PendingStart(Guid.NewGuid(), request);
        _pendingStarts[pending.OptimisticId] = pending;
        Merge(Starting(pending));
        _ = StartCoreAsync(pending, _disposeCts.Token);
    }

    public async Task RetryAsync(BusinessOnboardingOperationResponse operation)
    {
        if (_pendingStarts.TryGetValue(operation.Id, out var pending))
        {
            Merge(Starting(pending));
            await StartCoreAsync(pending, _disposeCts.Token);
            return;
        }
        var response = await http.PostAsync(
            $"api/business-onboarding/operations/{operation.Id:D}/retry", null, _disposeCts.Token);
        await MergeResponseAsync(response, "Business onboarding could not be retried.");
    }

    public async Task DismissAsync(BusinessOnboardingOperationResponse operation)
    {
        if (_pendingStarts.Remove(operation.Id))
        {
            Remove(operation.Id);
            return;
        }
        var response = await http.PostAsync(
            $"api/business-onboarding/operations/{operation.Id:D}/dismiss", null, _disposeCts.Token);
        if (response.IsSuccessStatusCode) Remove(operation.Id);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var serverOperations = await http.GetFromJsonAsync<IReadOnlyList<BusinessOnboardingOperationResponse>>(
                "api/business-onboarding/operations", cancellationToken) ?? [];
            var optimistic = Operations.Where(x => _pendingStarts.ContainsKey(x.Id));
            Operations = serverOperations.Concat(optimistic)
                .GroupBy(x => x.Id)
                .Select(x => x.OrderByDescending(y => y.UpdatedAt).First())
                .OrderBy(x => x.UpdatedAt)
                .ToList();
            Changed?.Invoke();
            StartPollingIfNeeded();
            ScheduleSuccessActions();
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
                "api/business-onboarding/operations", pending.Request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await ErrorMessageAsync(response, "Business onboarding could not be started."));
            var operation = await response.Content.ReadFromJsonAsync<BusinessOnboardingOperationResponse>(cancellationToken)
                ?? throw new InvalidOperationException("The business onboarding response was empty.");
            _pendingStarts.Remove(pending.OptimisticId);
            Operations = Operations.Where(x => x.Id != pending.OptimisticId).ToList();
            Merge(operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Merge(Starting(pending) with
            {
                Status = BusinessOnboardingOperationStatuses.Failed,
                Phase = "Onboarding could not start",
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
        var operation = await response.Content.ReadFromJsonAsync<BusinessOnboardingOperationResponse>(_disposeCts.Token)
            ?? throw new InvalidOperationException(fallback);
        Merge(operation);
    }

    private void OnRealtimeEvent(AppRealtimeEventEnvelope envelope)
    {
        if (envelope.EventType == AppRealtimeEvents.BusinessOnboardingOperationChanged)
            _ = RefreshAsync(_disposeCts.Token);
    }

    private void OnReconnected() => _ = RefreshAsync(_disposeCts.Token);

    private void StartPollingIfNeeded()
    {
        if (!Operations.Any(x => BusinessOnboardingOperationStatuses.IsActive(x.Status)) ||
            _pollTask is { IsCompleted: false }) return;
        _pollTask = PollAsync(_disposeCts.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!cancellationToken.IsCancellationRequested &&
               Operations.Any(x => BusinessOnboardingOperationStatuses.IsActive(x.Status)))
        {
            if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            await RefreshAsync(cancellationToken);
        }
    }

    private void ScheduleSuccessActions()
    {
        foreach (var operation in Operations.Where(x => x.Status == BusinessOnboardingOperationStatuses.Succeeded))
        {
            if (_businessContextRefreshed.Add(operation.Id))
                _ = businessContext.RefreshAsync();
            if (_autoDismissScheduled.Add(operation.Id))
                _ = AutoDismissAsync(operation, _disposeCts.Token);
        }
    }

    private async Task AutoDismissAsync(
        BusinessOnboardingOperationResponse operation,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        await DismissAsync(operation);
    }

    private void Merge(BusinessOnboardingOperationResponse operation)
    {
        Operations = Operations.Where(x => x.Id != operation.Id)
            .Append(operation).OrderBy(x => x.UpdatedAt).ToList();
        Changed?.Invoke();
        StartPollingIfNeeded();
        ScheduleSuccessActions();
    }

    private void Remove(Guid operationId)
    {
        Operations = Operations.Where(x => x.Id != operationId).ToList();
        Changed?.Invoke();
    }

    private static BusinessOnboardingOperationResponse Starting(PendingStart pending) => new(
        pending.OptimisticId,
        pending.Request.BusinessName,
        "Chief of Staff",
        BusinessOnboardingOperationStatuses.Starting,
        "Starting onboarding",
        $"Saving the onboarding plan for {pending.Request.BusinessName}…",
        0,
        0,
        null,
        null,
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

    private sealed record PendingStart(Guid OptimisticId, StartBusinessOnboardingRequest Request);
    private sealed record ApiError(string? Error, string? Message);
}
