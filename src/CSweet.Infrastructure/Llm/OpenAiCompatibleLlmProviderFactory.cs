using CSweet.AI.Providers;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace CSweet.Infrastructure.Llm;

public sealed class OpenAiCompatibleLlmProviderFactory : ILlmProviderFactory
{
    private const string LocalApiKeyPlaceholder = "local-provider";

    private readonly CSweetDbContext _dbContext;
    private readonly ILlmProviderSecretStore _secretStore;
    private readonly ILogger<OpenAiCompatibleLlmProviderFactory> _logger;
    private readonly TimeSpan _networkTimeout;

    public OpenAiCompatibleLlmProviderFactory(
        CSweetDbContext dbContext,
        ILlmProviderSecretStore secretStore,
        ILogger<OpenAiCompatibleLlmProviderFactory> logger,
        IConfiguration? configuration = null)
    {
        _dbContext = dbContext;
        _secretStore = secretStore;
        _logger = logger;
        _networkTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration?.GetValue<int?>("CSweet:Llm:Queue:GenerationTimeoutSeconds") ?? 900, 1, 86400));
    }

    public async Task<IChatClient> CreateChatClientAsync(
        Guid providerProfileId,
        CancellationToken cancellationToken = default) =>
        await CreateChatClientAsync(providerProfileId, model: null, cancellationToken);

    public async Task<IChatClient> CreateChatClientAsync(
        Guid providerProfileId,
        string? model,
        CancellationToken cancellationToken = default)
    {
        var profile = await _dbContext.LlmProviderProfiles
            .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);

        if (profile is null)
        {
            _logger.LogWarning(
                "Could not create chat client because provider profile {ProviderProfileId} was not found.",
                providerProfileId);

            throw new InvalidOperationException("Provider profile was not found.");
        }

        if (!profile.ProviderType.UsesOpenAiCompatibleApi())
        {
            _logger.LogWarning(
                "Could not create chat client for provider profile {ProviderProfileId}: unsupported provider type {ProviderType}.",
                providerProfileId,
                profile.ProviderType);

            throw new NotSupportedException($"Provider type '{profile.ProviderType}' is not supported by the OpenAI-compatible factory.");
        }

        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var configuredEndpoint) ||
            configuredEndpoint.Scheme is not ("http" or "https"))
        {
            _logger.LogWarning(
                "Could not create chat client for provider profile {ProviderProfileId}: invalid base URL {BaseUrl}.",
                providerProfileId,
                profile.BaseUrl);

            throw new InvalidOperationException("Provider base URL is invalid.");
        }

        var endpoint = NormalizeBaseEndpoint(configuredEndpoint, profile.ProviderType);
        var selectedModel = string.IsNullOrWhiteSpace(model)
            ? profile.DefaultChatModel
            : model.Trim();
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            throw new InvalidOperationException("No chat model was selected for this provider request.");
        }
        var expectedModelsEndpoint = new Uri(endpoint, "models");
        var expectedChatCompletionsEndpoint = new Uri(endpoint, "chat/completions");

        _logger.LogInformation(
            "Creating OpenAI-compatible chat client for provider profile {ProviderProfileId}. Type {ProviderType}. StoredBaseUrl {StoredBaseUrl}. NormalizedEndpoint {NormalizedEndpoint}. ExpectedModelsEndpoint {ExpectedModelsEndpoint}. ExpectedChatCompletionsEndpoint {ExpectedChatCompletionsEndpoint}. Model {Model}. SupportsStreaming {SupportsStreaming}.",
            providerProfileId,
            profile.ProviderType,
            profile.BaseUrl,
            endpoint,
            expectedModelsEndpoint,
            expectedChatCompletionsEndpoint,
            selectedModel,
            profile.SupportsStreaming);

        var apiKey = await ResolveApiKeyAsync(profile, cancellationToken);
        var options = new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = _networkTimeout };
        var chatClient = new ChatClient(selectedModel, new ApiKeyCredential(apiKey), options);

        return chatClient.AsIChatClient();
    }

    private async Task<string> ResolveApiKeyAsync(LlmProviderProfile profile, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.ApiKeySecretName))
        {
            var apiKey = await _secretStore.GetAsync(profile.ApiKeySecretName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return apiKey;
            }
        }

        return profile.ProviderType.IsLocalRuntime()
            ? LocalApiKeyPlaceholder
            : string.Empty;
    }

    private static Uri NormalizeBaseEndpoint(Uri configuredEndpoint, LlmProviderType providerType)
    {
        var builder = new UriBuilder(configuredEndpoint)
        {
            Path = configuredEndpoint.AbsolutePath.TrimEnd('/') + "/"
        };

        if (providerType.IsLocalRuntime() &&
            string.Equals(builder.Path, "/", StringComparison.Ordinal))
        {
            builder.Path = "/v1/";
        }

        if (IsRunningInContainer() && configuredEndpoint.IsLoopback)
        {
            builder.Host = "host.docker.internal";
        }

        return builder.Uri;
    }

    private static bool IsRunningInContainer() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
