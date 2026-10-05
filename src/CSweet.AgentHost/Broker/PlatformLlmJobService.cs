using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using AgentWorkKind = CSweet.Domain.Setup.AgentWorkKind;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformLlmJobOptions
{
    public const string SectionName = "CSweet:Llm:Queue";
    public int MaximumConcurrentRequests { get; set; } = 1;
    public int MaximumQueuedRequests { get; set; } = 256;
    /// <summary>
    /// Optional wall-clock cap on a single generation. Zero (the default) means no limit: long generations,
    /// such as large outputs from local models, are legitimate. Liveness comes from the caller instead: an
    /// unpolled request is cancelled after 60 seconds, and work leases, user cancellation and shutdown still apply.
    /// </summary>
    public int GenerationTimeoutSeconds { get; set; }
    /// <summary>Maximum serialized inference bytes; zero disables this limit.</summary>
    public int MaximumRequestBytes { get; set; }
    public int MaximumMessageCount { get; set; } = 512;
    /// <summary>Maximum message characters; zero disables this limit.</summary>
    public int MaximumMessageCharacters { get; set; }
    public int MaximumToolCount { get; set; } = 256;
    public int DefaultMaximumOutputTokens { get; set; } = 32_768;
}

public interface IPlatformLlmJobExecutor
{
    IAsyncEnumerable<CapabilityResult> ExecuteAsync(AgentSession session, RequestCapability request, CancellationToken token);
}

public sealed class PlatformLlmJobExecutor(PlatformLlmCapabilityHandler handler) : IPlatformLlmJobExecutor
{
    public IAsyncEnumerable<CapabilityResult> ExecuteAsync(AgentSession session, RequestCapability request, CancellationToken token) =>
        handler.StreamAsync(session, request, token, providerSlotAcquired: true);
}

/// <summary>Short authenticated polling keeps buffered Office broker calls out of the inference lifetime.</summary>
public sealed class PlatformLlmJobService(IServiceScopeFactory scopes, PlatformLlmJobOptions options,
    TimeProvider clock, ILogger<PlatformLlmJobService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Job> jobs = new();
    private readonly ConcurrentDictionary<Guid, ProviderInferenceGate> providers = new();
    private readonly SemaphoreSlim budgetGate = new(1, 1);
    private readonly Dictionary<Guid, DateTimeOffset> runtimeAccounted = new();
    private CancellationToken shutdown;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal Task<IDisposable> AcquireProviderAsync(Guid provider, CancellationToken token, bool delivery = false)
    {
        var gate = providers.GetOrAdd(provider, _ => new(options.MaximumConcurrentRequests, options.MaximumQueuedRequests));
        return gate.AcquireAsync(delivery, token);
    }

    internal sealed class Job
    {
        public required Guid Id { get; init; }
        public required AgentSession Session { get; init; }
        public required Guid WorkId { get; init; }
        public required int Attempt { get; init; }
        public required string LeaseToken { get; init; }
        public required string Key { get; init; }
        public required string Hash { get; init; }
        public required Guid Provider { get; init; }
        public required RequestCapability Request { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public bool IsDelivery { get; init; }
        public DateTimeOffset LastPoll { get; set; }
        public DateTimeOffset AccountedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string State { get; set; } = "Received";
        public string? Error { get; set; }
        public string? FailureCode { get; set; }
        public bool Retryable { get; set; }
        public PlatformLlmResultBuffer Results { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Execution { get; set; }
        public object Sync { get; } = new();
    }

    public async Task<Guid> StartAsync(AgentSession session, Guid workId, int attempt, string leaseToken,
        string key, JsonElement arguments, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ArgumentException("An inference request key is required.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (options.MaximumRequestBytes > 0 && bytes.Length > options.MaximumRequestBytes)
            throw new ArgumentException($"The inference payload exceeds the configured {options.MaximumRequestBytes}-byte limit.");
        var provider = arguments.GetProperty("providerProfileId").GetGuid();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await budgetGate.WaitAsync(token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentWorkInbox>().ReadDeadlineAsync(
                Persisted(session), workId, attempt, leaseToken, token);
            // Priority is derived from authenticated durable work, never caller telemetry.
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var work = await db.AgentWorkItems.SingleAsync(x => x.Id == workId, token);
            var existing = jobs.Values.SingleOrDefault(x => x.Session.RuntimeInstanceId == session.RuntimeInstanceId && x.Key == key);
            if (existing is not null)
            {
                if (existing.Hash != hash || existing.WorkId != workId || existing.Attempt != attempt)
                    throw new InvalidOperationException("The inference key belongs to a different request.");
                return existing.Id;
            }
            if (jobs.Values.Count(x => x.FinishedAt is null) >= options.MaximumQueuedRequests + options.MaximumConcurrentRequests ||
                jobs.Values.Any(x => x.WorkId == workId && x.FinishedAt is null))
                throw new InvalidOperationException("The inference queue is full or this work already has an active request.");
            var now = clock.GetUtcNow();
            var job = new Job { Id = Guid.NewGuid(), Session = session, WorkId = workId, Attempt = attempt,
                LeaseToken = leaseToken, Key = key, Hash = hash, Provider = provider, CreatedAt = now,
                LastPoll = now, AccountedAt = now,
                IsDelivery = work.Kind == AgentWorkKind.Capability && work.SourceType == "WorkStageExecution" ||
                    work.Kind == AgentWorkKind.Event && (
                        work.SourceType == "agent-coordination" && work.Name == AgentCoordinationEvents.TurnRequested ||
                        work.SourceType == "platform-event" && work.Name == CSweet.WorkManagement.Contracts.PersonalTodoEvents.Available),
                Request = new() { RequestId = Guid.NewGuid().ToString("N"), Capability = PlatformCapabilities.LlmChatStream,
                    Payload = JsonPayload.From(bytes) } };
            jobs[job.Id] = job;
            job.Execution = RunAsync(job);
            return job.Id;
        }
        finally { budgetGate.Release(); }
    }

    public async Task<object> ReadAsync(AgentSession session, Guid id, int after, CancellationToken token)
    {
        var job = Owned(session, id);
        if (after < 0) throw new ArgumentException("Invalid inference cursor.");
        lock (job.Sync) job.LastPoll = clock.GetUtcNow();
        // Buffered transports can drain tiny token pages faster than the session request
        // budget permits. Coalesce output for one second, leaving room for lease/control calls.
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        var deadline = await AccountWaitAsync(job, token);
        lock (job.Sync)
        {
            // The next cursor acknowledges the previous response. Keep its data until then
            // so a lost response can be requested again without loss or duplication.
            var page = job.Results.Read(after);
            return new { jobId = id, state = job.State, workId = job.WorkId, workDeadline = deadline,
                next = after + page.Length, completed = job.FinishedAt.HasValue && after + page.Length == job.Results.End,
                error = job.Error, retryable = job.Retryable,
                failureCode = job.Error is null ? null : job.FailureCode ?? (job.Retryable ? "llm.provider_unavailable" : "llm.request_failed"),
                chunks = page.Select(x => new { x.Succeeded, x.HasMore, x.Sequence, x.Error, x.FailureCode, x.Retryable,
                    payload = x.Payload.IsEmpty ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(x.Payload.Span) }) };
        }
    }

    internal static CapabilityResult[] SelectResultPage(IReadOnlyList<CapabilityResult> results, int after)
    {
        var page = new List<CapabilityResult>();
        var payloadBytes = 0;
        for (var i = after; i < results.Count && page.Count < 256; i++)
        {
            var result = results[i];
            // Preserve progress for a single large chunk; the existing transport frame limit
            // still applies. Ordinary pages leave ample room for JSON and response metadata.
            if (page.Count > 0 && payloadBytes + result.Payload.Length > 64 * 1024) break;
            page.Add(result);
            payloadBytes += result.Payload.Length;
        }
        return page.ToArray();
    }
    public void Cancel(AgentSession session, Guid id) => Owned(session, id).Cancellation.Cancel();

    private Job Owned(AgentSession session, Guid id)
    {
        if (!jobs.TryGetValue(id, out var job) || job.Session.RuntimeInstanceId != session.RuntimeInstanceId ||
            job.Session.InstallationId != session.InstallationId || job.Session.BusinessId != session.BusinessId)
            throw new UnauthorizedAccessException("The inference request is unavailable to this runtime.");
        return job;
    }

    private async Task RunAsync(Job job)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, shutdown);
        var token = cancellation.Token;
        try
        {
            await SetStateAsync(job, "Queued", token);
            using var permit = await AcquireProviderAsync(job.Provider, token, job.IsDelivery);
            await AccountWaitAsync(job, token);
            await SetStateAsync(job, "Generating", token);
            if (options.GenerationTimeoutSeconds > 0)
                cancellation.CancelAfter(TimeSpan.FromSeconds(options.GenerationTimeoutSeconds));
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IPlatformLlmJobExecutor>();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var work = await db.AgentWorkItems.SingleAsync(x => x.Id == job.WorkId, token);
            var context = await AgentTicketFeedback.ReadContextAsync(db, work, token);
            var request = job.Request;
            if (context is not null)
            {
                var payload = JsonNode.Parse(request.Payload.ToStringUtf8())!.AsObject();
                var messages = payload["messages"]!.AsArray();
                // A user message keeps ticket discussion below system/developer instructions.
                // Prepend rather than splitting a tool-call / tool-result exchange.
                messages.Insert(0, new JsonObject { ["role"] = "user", ["text"] = context });
                request = new RequestCapability
                {
                    RequestId = request.RequestId, RequestingAgentId = request.RequestingAgentId,
                    Capability = request.Capability, ContentType = request.ContentType,
                    Payload = JsonPayload.FromUtf8(payload.ToJsonString())
                };
            }
            using var attribution = new InferenceExecutionAttribution(job.WorkId, job.Id, job.Attempt).Enter();
            await foreach (var result in handler.ExecuteAsync(job.Session, request, token))
            {
                lock (job.Sync)
                {
                    job.Results.Add(result);
                    if (!result.Succeeded)
                    {
                        job.Error = result.Error ?? "The provider request failed.";
                        job.Retryable = result.Retryable == true;
                        job.FailureCode = result.FailureCode;
                    }
                }
            }
            await AccountWaitAsync(job, token);
            await SetStateAsync(job, job.Error is null ? "Completed" : "Failed", token);
        }
        catch (OperationCanceledException)
        {
            var callerCancelled = job.Cancellation.IsCancellationRequested || shutdown.IsCancellationRequested;
            job.Retryable = !callerCancelled;
            job.Error = callerCancelled
                ? "The inference request was cancelled or its caller disconnected."
                : "The provider exceeded its generation time limit.";
            // A provider timeout is a failed attempt eligible for cooldown recovery,
            // not a deliberate cancellation of the caller's work.
            await SetStateAsync(job, callerCancelled ? "Cancelled" : "Failed", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Inference job {JobId} failed.", job.Id);
            job.Error = LlmProviderFailureMessage.From(exception);
            job.Retryable = LlmProviderFailureMessage.IsTransient(exception);
            job.FailureCode = LlmProviderFailureMessage.CodeFrom(exception);
            await SetStateAsync(job, "Failed", CancellationToken.None);
        }
        finally { lock (job.Sync) job.FinishedAt = clock.GetUtcNow(); }
    }

    private async Task<DateTimeOffset> AccountWaitAsync(Job job, CancellationToken token)
    {
        await budgetGate.WaitAsync(token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var inbox = scope.ServiceProvider.GetRequiredService<AgentWorkInbox>();
            await inbox.ReadDeadlineAsync(Persisted(job.Session), job.WorkId, job.Attempt, job.LeaseToken, token);
            var work = await db.AgentWorkItems.SingleAsync(x => x.Id == job.WorkId, token);
            var runtimeId = Guid.Parse(job.Session.RuntimeInstanceId);
            var runtime = await db.AgentRuntimeInstances.Include(x => x.AgentInstallation)!.ThenInclude(x => x!.Grant)
                .SingleAsync(x => x.Id == runtimeId, token);
            var now = clock.GetUtcNow();
            if (runtime.Status != AgentRuntimeStatus.Running || runtime.RuntimeDeadlineAt <= now ||
                runtime.AgentInstallation?.IsEnabled != true ||
                runtime.AgentInstallation.Grant?.GrantRevision != job.Session.Grant.Revision)
                throw new UnauthorizedAccessException("The inference runtime is no longer active.");
            if (job.FinishedAt is null)
            {
                // Inference has its own generation deadline. Both queue and provider waiting
                // are platform-owned time, not agent execution. Never revive an expired lease.
                var delta = now - job.AccountedAt;
                var allowance = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentRuntimeManagerOptions>>()
                    .Value.InferenceWaitAllowanceSeconds;
                if (now - job.CreatedAt > TimeSpan.FromSeconds(Math.Clamp(allowance, 0, 86400)))
                    throw new InvalidOperationException("The infrastructure inference waiting allowance was exhausted.");
                work.DeadlineAt = ExtendDeadline(work.DeadlineAt, delta);
                var previous = runtimeAccounted.GetValueOrDefault(runtimeId, job.AccountedAt);
                if (previous < job.AccountedAt) previous = job.AccountedAt;
                if (now > previous) runtime.RuntimeDeadlineAt += now - previous;
                if (runtime.IdleDeadlineAt is not null) runtime.IdleDeadlineAt = now.AddMinutes(5);
                await db.SaveChangesAsync(token);
                job.AccountedAt = now;
                runtimeAccounted[runtimeId] = now;
            }
            return work.DeadlineAt;
        }
        finally { budgetGate.Release(); }
    }

    internal static DateTimeOffset ExtendDeadline(DateTimeOffset deadline, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero ? deadline :
        elapsed >= DateTimeOffset.MaxValue - deadline ? DateTimeOffset.MaxValue : deadline + elapsed;

    private async Task SetStateAsync(Job job, string state, CancellationToken token)
    {
        lock (job.Sync) job.State = state;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            await WorkExecutionConflictRetry.RunAsync(db, async () =>
            {
                var row = await db.AgentRunLogs.SingleOrDefaultAsync(x => x.Id == job.Id, token);
                if (row is null)
                {
                    row = new() { Id = job.Id, OrganizationId = Guid.Parse(job.Session.BusinessId),
                        AgentInstallationId = Guid.Parse(job.Session.InstallationId), AgentKey = job.Session.AgentId,
                        ProviderProfileId = job.Provider, StartedAt = job.CreatedAt, PromptHash = job.Hash,
                        InvocationKind = "llm-queue", MeasurementKind = "Queue", AgentWorkItemId = job.WorkId };
                    db.AgentRunLogs.Add(row);
                    await CSweet.Infrastructure.Analytics.InferenceAttribution.CaptureAsync(db, row, job.WorkId, token, job.Attempt);
                }
                row.Status = state;
                row.FailureMessage = job.Error;
                row.DurationMs = (long)(clock.GetUtcNow() - job.CreatedAt).TotalMilliseconds;
                if (state is "Completed" or "Failed" or "Cancelled") row.CompletedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(token);
            }, token);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not persist inference status for {JobId}.", job.Id); }
    }

    // Jobs live in memory. Rows left open by a previous AgentHost process (crash or restart) can never
    // complete, and activity views would show them as phantom "waiting for model capacity" work. The
    // owning agent work recovers through its durable lease, so the stale rows are only closed here.
    private async Task CloseOrphanedJobsAsync(CancellationToken token)
    {
        try
        {
            var startedAt = clock.GetUtcNow();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var open = await db.AgentRunLogs.Where(x => x.InvocationKind == "llm-queue" &&
                x.CompletedAt == null && x.StartedAt < startedAt).ToListAsync(token);
            var orphans = open.Where(x => !jobs.ContainsKey(x.Id)).ToList();
            if (orphans.Count == 0) return;
            foreach (var row in orphans)
            {
                row.Status = "Cancelled";
                row.FailureMessage ??= "AgentHost restarted before this inference finished; the owning work retries through its durable lease.";
                row.CompletedAt = startedAt;
            }
            await db.SaveChangesAsync(token);
            logger.LogInformation("Closed {Count} inference job record(s) orphaned by a previous AgentHost process.", orphans.Count);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not close inference job records orphaned by a previous AgentHost process.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        shutdown = stoppingToken;
        await CloseOrphanedJobsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var job in jobs.Values)
                {
                    var now = clock.GetUtcNow();
                    if (job.FinishedAt is { } finished)
                    {
                        if (now - finished > TimeSpan.FromMinutes(5) && jobs.TryRemove(job.Id, out _))
                            job.Cancellation.Dispose();
                        continue;
                    }
                    if (now - job.LastPoll > TimeSpan.FromSeconds(60)) job.Cancellation.Cancel();
                    else
                    {
                        try { await AccountWaitAsync(job, stoppingToken); }
                        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogWarning(exception, "Inference lease validation failed for {JobId}.", job.Id);
                            job.Cancellation.Cancel();
                        }
                    }
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var job in jobs.Values) job.Cancellation.Cancel();
            await Task.WhenAll(jobs.Values.Select(x => x.Execution ?? Task.CompletedTask));
        }
    }

    private static McpAgentSession Persisted(AgentSession session) => new()
    {
        RuntimeInstanceId = Guid.Parse(session.RuntimeInstanceId), AgentInstallationId = Guid.Parse(session.InstallationId),
        OrganizationId = session.BusinessId
    };
}
