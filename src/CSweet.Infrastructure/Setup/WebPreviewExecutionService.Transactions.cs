using CSweet.Infrastructure.WorkManagement;
using CSweet.WebHost.Contracts;

namespace CSweet.Infrastructure.Setup;

public sealed partial class WebPreviewExecutionService
{
    public Task<PreviewOperation> StartAsync(Guid organizationId, Guid installationId, PreviewRequest request, CancellationToken token) =>
        AttemptAsync(() => StartCoreAsync(organizationId, installationId, request, token));
    public Task<PreviewOperation> StopAsync(Guid organizationId, Guid installationId, Guid previewId, CancellationToken token) =>
        AttemptAsync(() => StopCoreAsync(organizationId, installationId, previewId, token));
    public Task<WebHostCommandPollReceipt> PollAsync(SignedWebHostMessage message, CancellationToken token) =>
        AttemptAsync(() => PollCoreAsync(message, token));
    public async Task<WebHostCommandResultReceipt> CompleteAsync(SignedWebHostMessage message, CancellationToken token)
    {
        try { return await AttemptAsync(() => CompleteCoreAsync(message, token)); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            // The host can acknowledge a discarded, invalid outcome without blocking its durable outbox.
            // Reauthenticate after discarding every tracked mutation from the rejected ingestion attempt.
            return await AttemptAsync(() => RejectDeliveredResultAsync(message, token));
        }
    }
    public Task<PreparedWebPreviewArtifact> ArtifactAsync(SignedWebHostMessage message, CancellationToken token) =>
        AttemptAsync(() => ArtifactCoreAsync(message, token));
    private async Task<T> AttemptAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch { db.ChangeTracker.Clear(); throw; }
    }
}
