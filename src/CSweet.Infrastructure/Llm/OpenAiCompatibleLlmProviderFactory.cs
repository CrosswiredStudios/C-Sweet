using CSweet.AI.Providers;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
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
    private readonly IConfiguration? _configuration;
    internal PipelineTransport? TransportOverride { get; init; }

    public OpenAiCompatibleLlmProviderFactory(
        CSweetDbContext dbContext,
        ILlmProviderSecretStore secretStore,
        ILogger<OpenAiCompatibleLlmProviderFactory> logger,
        IConfiguration? configuration = null)
    {
        _dbContext = dbContext;
        _secretStore = secretStore;
        _logger = logger;
        _configuration = configuration;
        // The provider connection never imposes its own generation limit; a configured positive
        // CSweet:Llm:Queue:GenerationTimeoutSeconds is the only optional cap.
        var generationTimeoutSeconds = configuration?.GetValue<int?>("CSweet:Llm:Queue:GenerationTimeoutSeconds") ?? 0;
        _networkTimeout = generationTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(Math.Min(generationTimeoutSeconds, 86400))
            : Timeout.InfiniteTimeSpan;
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
        var profile = await _dbContext.LlmProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == providerProfileId, cancellationToken);

        if (profile is null)
        {
            _logger.LogWarning(
                "Could not create chat client because provider profile {ProviderProfileId} was not found.",
                providerProfileId);

            throw new InvalidOperationException("Provider profile was not found.");
        }
        if (!profile.IsEnabled) throw new ProviderDispatchDeniedException();

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
        if (TransportOverride is not null) options.Transport = TransportOverride;
        var expectedConfiguration = ProviderDispatchConfiguration.Fingerprint(profile);
        options.AddPolicy(new ProviderDispatchPolicy(async token =>
        {
            var current = await _dbContext.LlmProviderProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == profile.Id, token);
            if (current is null || !current.IsEnabled || ProviderDispatchConfiguration.Fingerprint(current) != expectedConfiguration ||
                !string.Equals(await ResolveApiKeyAsync(current, token), apiKey, StringComparison.Ordinal))
                throw new ProviderDispatchDeniedException();
        }), PipelinePosition.BeforeTransport);
        var chatClient = new ChatClient(selectedModel, new ApiKeyCredential(apiKey), options);

        IChatClient adapted = AdaptChatClient(chatClient, profile.SupportsStreaming);
        var ensureUserQuery = _configuration?.GetValue<bool?>(
            $"CSweet:Llm:Compatibility:Providers:{profile.Id:D}:EnsureUserMessage") ??
            _configuration?.GetValue<bool?>("CSweet:Llm:Compatibility:EnsureUserMessage") ??
            (profile.ProviderType.IsLocalRuntime() || profile.ProviderType is LlmProviderType.OpenAiCompatible or LlmProviderType.Custom);
        if (ensureUserQuery) adapted = new UserQueryChatClient(adapted);
        return new DispatchManagedChatClient(ApplyProviderDefaults(adapted, profile.MaxOutputTokens));
    }

    internal static IChatClient AdaptChatClient(ChatClient client, bool supportsStreaming = true) =>
        new ProviderResponseContractChatClient(new ReasoningContentChatClient(client), supportsStreaming);

    internal static IChatClient ApplyProviderDefaults(IChatClient client, int? maxOutputTokens) =>
        new ConfigureOptionsChatClient(client, options =>
        {
            // An explicit per-call budget takes precedence over the provider default.
            // ConfigureOptionsChatClient clones caller options before applying defaults.
            options.MaxOutputTokens ??= maxOutputTokens;
        });

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

        // Custom/self-hosted compatible endpoints may intentionally require no key.
        // The OpenAI SDK still requires a non-empty credential to construct its client,
        // unlike the HTTP client used by setup's connection test. This placeholder is
        // not a credential or an authentication bypass; the endpoint enforces its auth.
        return profile.ProviderType.IsLocalRuntime() ||
            profile.ProviderType is LlmProviderType.Custom or LlmProviderType.OpenAiCompatible
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
