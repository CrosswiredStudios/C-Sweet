using CSweet.Contracts.GenAi;
using CSweet.Domain.Setup;

namespace CSweet.Plugin.SDK;

public static class GenAiBrokerClientExtensions
{
    public static Task<GenAiJobResponse> GenerateImageAsync(this PluginBrokerClient client, GenAiMediaRequest request,
        CancellationToken cancellationToken = default) =>
        client.InvokeAsync<GenAiMediaRequest, GenAiJobResponse>(GenAiCapabilities.ImageGenerate, request, cancellationToken: cancellationToken);

    public static Task<GenAiJobResponse> EditImageAsync(this PluginBrokerClient client, GenAiMediaRequest request,
        CancellationToken cancellationToken = default) =>
        client.InvokeAsync<GenAiMediaRequest, GenAiJobResponse>(GenAiCapabilities.ImageEdit, request, cancellationToken: cancellationToken);

    public static Task<GenAiJobResponse> GenerateVideoAsync(this PluginBrokerClient client, GenAiMediaRequest request,
        CancellationToken cancellationToken = default) =>
        client.InvokeAsync<GenAiMediaRequest, GenAiJobResponse>(GenAiCapabilities.VideoGenerate, request, cancellationToken: cancellationToken);

    public static Task<GenAiJobResponse> EditVideoAsync(this PluginBrokerClient client, GenAiMediaRequest request,
        CancellationToken cancellationToken = default) =>
        client.InvokeAsync<GenAiMediaRequest, GenAiJobResponse>(GenAiCapabilities.VideoEdit, request, cancellationToken: cancellationToken);

    public static Task<GenAiJobResponse> GetGenAiJobAsync(this PluginBrokerClient client, Guid jobId,
        CancellationToken cancellationToken = default) =>
        client.InvokeAsync<GenAiJobLookupRequest, GenAiJobResponse>(
            GenAiCapabilities.JobRead, new(jobId), cancellationToken: cancellationToken);

    public static Task<GenAiJobResponse> CancelGenAiJobAsync(this PluginBrokerClient client, Guid jobId,
        CancellationToken cancellationToken = default) =>
        client.InvokeAsync<GenAiJobLookupRequest, GenAiJobResponse>(
            GenAiCapabilities.JobCancel, new(jobId), cancellationToken: cancellationToken);

    public static async Task<GenAiJobResponse> WaitForGenAiJobAsync(this PluginBrokerClient client, Guid jobId,
        TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);
        while (true)
        {
            var job = await client.GetGenAiJobAsync(jobId, cancellationToken);
            if (job.Status is GenAiJobStatus.Succeeded or GenAiJobStatus.Failed or GenAiJobStatus.Canceled) return job;
            await Task.Delay(interval, cancellationToken);
        }
    }
}
