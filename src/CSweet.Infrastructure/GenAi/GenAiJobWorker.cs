using System.Text.Json;
using CSweet.AI.Providers;
using CSweet.Application.GenAi;
using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.GenAi;

public sealed class GenAiJobWorker(IServiceScopeFactory scopes, ILogger<GenAiJobWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _workerId = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneAsync(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "GenAI job worker failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var now = DateTimeOffset.UtcNow;
        var candidate = await db.GenAiJobs.AsNoTracking()
            .Where(x => (x.Status == GenAiJobStatus.Queued || x.Status == GenAiJobStatus.Running) &&
                (!x.LeaseExpiresAt.HasValue || x.LeaseExpiresAt < now))
            .OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.Status }).FirstOrDefaultAsync(token);
        if (candidate is null) return false;
        var claimed = await db.GenAiJobs.Where(x => x.Id == candidate.Id && x.Status == candidate.Status &&
                (!x.LeaseExpiresAt.HasValue || x.LeaseExpiresAt < now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseOwner, _workerId)
                .SetProperty(x => x.LeaseExpiresAt, now.AddMinutes(2)), token);
        if (claimed == 0) return true;
        var job = await db.GenAiJobs.Include(x => x.OperationConfiguration)!.ThenInclude(x => x!.ProviderProfile)
            .SingleAsync(x => x.Id == candidate.Id, token);
        var operation = job.OperationConfiguration!;
        var profile = operation.ProviderProfile!;
        var adapter = scope.ServiceProvider.GetServices<IGenAiProviderAdapter>().Single(x => x.ProviderType == profile.ProviderType);
        var secrets = scope.ServiceProvider.GetRequiredService<ILlmProviderSecretStore>();
        var store = scope.ServiceProvider.GetRequiredService<IMediaAssetStore>();
        var key = profile.ApiKeySecretName is null ? null : await secrets.GetAsync(profile.ApiKeySecretName, token);

        try
        {
            if (job.Status == GenAiJobStatus.Queued)
            {
                job.Status = GenAiJobStatus.Running;
                job.StartedAt ??= DateTimeOffset.UtcNow;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                job.AttemptCount++;
                await db.SaveChangesAsync(token);

                var request = JsonSerializer.Deserialize<GenAiMediaRequest>(job.RequestJson, JsonOptions)
                    ?? throw new InvalidOperationException("GenAI job request is invalid.");
                var ids = (request.SourceAssetIds ?? []).Concat(request.MaskAssetId.HasValue ? [request.MaskAssetId.Value] : []).Distinct().ToList();
                var assets = await db.MediaAssets.AsNoTracking().Where(x => ids.Contains(x.Id) && x.OrganizationId == job.OrganizationId).ToListAsync(token);
                var inputs = new Dictionary<Guid, GenAiAdapterInput>();
                try
                {
                    foreach (var asset in assets)
                        inputs[asset.Id] = new(asset.Id, asset.FileName, asset.ContentType, await store.OpenReadAsync(asset.StorageKey, token));
                    var submission = await adapter.SubmitAsync(profile, operation, request, inputs, key, token);
                    job.ProviderJobId = submission.ProviderJobId;
                    if (submission.IsComplete) await CompleteAsync(db, store, job, submission.Outputs, token);
                    else
                    {
                        job.UpdatedAt = DateTimeOffset.UtcNow;
                        Release(job);
                        await db.SaveChangesAsync(token);
                    }
                }
                finally
                {
                    foreach (var input in inputs.Values) await input.Content.DisposeAsync();
                }
            }
            else if (!string.IsNullOrWhiteSpace(job.ProviderJobId))
            {
                var poll = await adapter.PollAsync(profile, operation, job.ProviderJobId, key, token);
                if (!poll.IsComplete)
                {
                    Release(job);
                    await db.SaveChangesAsync(token);
                    return true;
                }
                if (poll.Failed) await FailAsync(db, job, poll.ErrorCode ?? "provider_error", poll.ErrorMessage ?? "Provider job failed.", token);
                else await CompleteAsync(db, store, job, poll.Outputs, token);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.LogWarning(exception, "GenAI job {JobId} failed.", job.Id);
            if (IsTransient(exception) && job.AttemptCount < 3)
                await RetryAsync(db, job, token);
            else
                await FailAsync(db, job, "provider_error", "The GenAI provider could not complete this job.", token);
        }
        return true;
    }

    private static async Task RetryAsync(CSweetDbContext db, GenAiJob job, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(job.ProviderJobId))
            job.Status = GenAiJobStatus.Queued;
        else
            job.AttemptCount++;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        Release(job);
        AddAudit(db, job, "genai.job.retrying", "GenAI media job will retry after a transient provider failure.");
        await db.SaveChangesAsync(token);
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or TaskCanceledException;

    private static async Task CompleteAsync(CSweetDbContext db, IMediaAssetStore store, GenAiJob job, IReadOnlyList<GenAiAdapterOutput> outputs, CancellationToken token)
    {
        if (outputs.Count == 0) throw new InvalidOperationException("Provider returned no media outputs.");
        var persistedStatus = await db.GenAiJobs.AsNoTracking().Where(x => x.Id == job.Id).Select(x => x.Status).SingleAsync(token);
        if (persistedStatus == GenAiJobStatus.Canceled)
        {
            foreach (var output in outputs) await output.Content.DisposeAsync();
            return;
        }
        foreach (var output in outputs.Take(4))
        {
            await using var content = output.Content;
            var contentType = MediaAssetService.NormalizeContentType(output.ContentType);
            await MediaAssetService.ValidateSignatureAsync(content, contentType, token);
            var saved = await store.SaveAsync(output.FileName, content, token);
            db.MediaAssets.Add(new MediaAsset
            {
                Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, CreatingAgentInstallationId = job.AgentInstallationId,
                GenAiJobId = job.Id, FileName = Path.GetFileName(output.FileName), ContentType = contentType,
                SizeBytes = saved.SizeBytes, Sha256 = saved.Sha256, StorageKey = saved.StorageKey, CreatedAt = DateTimeOffset.UtcNow
            });
        }
        job.Status = GenAiJobStatus.Succeeded;
        job.CompletedAt = job.UpdatedAt = DateTimeOffset.UtcNow;
        job.RequestJson = "{}";
        Release(job);
        AddAudit(db, job, "genai.job.succeeded", "GenAI media job completed.");
        await db.SaveChangesAsync(token);
    }

    private static async Task FailAsync(CSweetDbContext db, GenAiJob job, string code, string message, CancellationToken token)
    {
        job.Status = GenAiJobStatus.Failed;
        job.ErrorCode = code;
        job.ErrorMessage = message.Length > 2048 ? message[..2048] : message;
        job.CompletedAt = job.UpdatedAt = DateTimeOffset.UtcNow;
        job.RequestJson = "{}";
        Release(job);
        AddAudit(db, job, "genai.job.failed", $"GenAI media job failed: {code}");
        await db.SaveChangesAsync(token);
    }

    private static void Release(GenAiJob job)
    {
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
    }

    private static void AddAudit(CSweetDbContext db, GenAiJob job, string eventType, string summary) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrganizationId = job.OrganizationId, EventType = eventType,
            EntityType = nameof(GenAiJob), EntityId = job.Id, Summary = summary, CreatedAt = DateTimeOffset.UtcNow
        });
}
