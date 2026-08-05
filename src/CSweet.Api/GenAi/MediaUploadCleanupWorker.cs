using CSweet.Application.GenAi;

namespace CSweet.Api.GenAi;

public sealed class MediaUploadCleanupWorker(
    IServiceScopeFactory scopes,
    ILogger<MediaUploadCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var uploads = scope.ServiceProvider.GetRequiredService<IResumableMediaUploadService>();
                var count = await uploads.CleanupExpiredAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Removed temporary data for {Count} expired media uploads.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to clean up expired media upload sessions.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
