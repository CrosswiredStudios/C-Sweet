using System.ClientModel.Primitives;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Domain.Setup;
using Microsoft.Extensions.AI;

namespace CSweet.Infrastructure.Llm;

internal sealed class ProviderDispatchPolicy(Func<CancellationToken, Task> authorizeProvider) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AuthorizeAsync(message.CancellationToken).GetAwaiter().GetResult();
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await AuthorizeAsync(message.CancellationToken);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private async Task AuthorizeAsync(CancellationToken token)
    {
        try
        {
            await authorizeProvider(token);
            await ProviderDispatchScope.AuthorizeCurrentAsync(token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch { ProviderDispatchScope.RecordDenial(); throw; }
        ProviderDispatchScope.RecordDispatch();
    }
}

public static class ProviderDispatchConfiguration
{
    public static string Fingerprint(LlmProviderProfile profile) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        profile.Id, profile.ProviderType, profile.BaseUrl, profile.ApiKeySecretName, profile.DefaultChatModel,
        profile.DefaultEmbeddingModel, profile.ContextWindowTokens, profile.MaxOutputTokens, profile.SupportsStreaming,
        profile.SupportsToolCalling, profile.SupportsStructuredOutput, profile.SupportsVision, profile.IsEnabled, profile.UpdatedAt
    })));
}

internal sealed class DispatchManagedChatClient(IChatClient inner) : DelegatingChatClient(inner), IProviderDispatchTransport
{
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IProviderDispatchTransport) ? this : base.GetService(serviceType, serviceKey);
}
