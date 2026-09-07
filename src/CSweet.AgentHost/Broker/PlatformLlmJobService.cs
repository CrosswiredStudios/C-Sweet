using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using CSweet.Agent.SDK;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformLlmJobOptions
{
    public const string SectionName = "CSweet:Llm:Queue";
    public int MaximumConcurrentRequests { get; set; } = 1;
    public int MaximumQueuedRequests { get; set; } = 256;
    public int GenerationTimeoutSeconds { get; set; } = 900;
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
    private readonly ConcurrentDictionary<Guid, ConcurrencyLimiter> providers = new();
    private readonly SemaphoreSlim budgetGate = new(1, 1);
    private readonly Dictionary<Guid, DateTimeOffset> runtimeAccounted = new();
    private CancellationToken shutdown;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<RateLimitLease> AcquireProviderAsync(Guid provider, CancellationToken token)
    {
        var gate = providers.GetOrAdd(provider, _ => new ConcurrencyLimiter(new()
        {
            PermitLimit = options.MaximumConcurrentRequests, QueueLimit = options.MaximumQueuedRequests,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));
        var permit = await gate.AcquireAsync(1, token);
        if (!permit.IsAcquired) { permit.Dispose(); throw new InvalidOperationException("The provider queue is full."); }
        return permit;
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
        public DateTimeOffset LastPoll { get; set; }
        public DateTimeOffset AccountedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string State { get; set; } = "Received";
        public string? Error { get; set; }
        public List<CapabilityResult> Results { get; } = [];
        public int ResultBytes { get; set; }
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
        if (bytes.Length > 1_048_576) throw new ArgumentException("The inference payload is too large.");
        var provider = arguments.GetProperty("providerProfileId").GetGuid();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await budgetGate.WaitAsync(token);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentWorkInbox>().ReadDeadlineAsync(
                Persisted(session), workId, attempt, leaseToken, token);
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
        var deadline = await AccountWaitAsync(job, token);
        lock (job.Sync)
        {
            if (after > job.Results.Count) throw new ArgumentException("Invalid inference cursor.");
            // Bound each response well below the Office broker's frame limit.
            var page = job.Results.Skip(after).Take(16).ToArray();
            return new { jobId = id, state = job.State, workId = job.WorkId, workDeadline = deadline,
                next = after + page.Length, completed = job.FinishedAt.HasValue && after + page.Length == job.Results.Count,
                error = job.Error, chunks = page.Select(x => new { x.Succeeded, x.HasMore, x.Sequence, x.Error,
                    payload = x.Payload.IsEmpty ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(x.Payload.Span) }) };
        }
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
            using var permit = await AcquireProviderAsync(job.Provider, token);
            await AccountWaitAsync(job, token);
            await SetStateAsync(job, "Generating", token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(options.GenerationTimeoutSeconds));
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IPlatformLlmJobExecutor>();
            await foreach (var result in handler.ExecuteAsync(job.Session, job.Request, token))
            {
                lock (job.Sync)
                {
                    job.ResultBytes += result.Payload.Length;
                    if (job.ResultBytes > 16 * 1024 * 1024 || job.Results.Count >= 32768)
                        throw new InvalidOperationException("The inference result exceeded its bounded buffer.");
                    job.Results.Add(result);
                    if (!result.Succeeded) job.Error = result.Error ?? "The provider request failed.";
                }
            }
            await AccountWaitAsync(job, token);
            await SetStateAsync(job, job.Error is null ? "Completed" : "Failed", token);
        }
        catch (OperationCanceledException)
        {
            job.Error = job.Cancellation.IsCancellationRequested || shutdown.IsCancellationRequested
                ? "The inference request was cancelled or its caller disconnected."
                : "The provider exceeded its generation time limit.";
            await SetStateAsync(job, "Cancelled", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Inference job {JobId} failed.", job.Id);
            job.Error = "The inference request failed. Review the provider/runtime diagnostics.";
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
                work.DeadlineAt += delta;
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

    private async Task SetStateAsync(Job job, string state, CancellationToken token)
    {
        lock (job.Sync) job.State = state;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
            var row = await db.AgentRunLogs.SingleOrDefaultAsync(x => x.Id == job.Id, token);
            if (row is null)
            {
                row = new() { Id = job.Id, OrganizationId = Guid.Parse(job.Session.BusinessId),
                    AgentInstallationId = Guid.Parse(job.Session.InstallationId), AgentKey = job.Session.AgentId,
                    ProviderProfileId = job.Provider, StartedAt = job.CreatedAt, PromptHash = job.Hash,
                    InvocationKind = "llm-queue" };
                db.AgentRunLogs.Add(row);
            }
            row.Status = state;
            row.FailureMessage = job.Error;
            row.DurationMs = (long)(clock.GetUtcNow() - job.CreatedAt).TotalMilliseconds;
            if (state is "Completed" or "Failed" or "Cancelled") row.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(token);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not persist inference status for {JobId}.", job.Id); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        shutdown = stoppingToken;
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
