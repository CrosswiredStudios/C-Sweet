using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AI.Providers;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace CSweet.AgentHost.Broker;

public sealed class PlatformLlmCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CSweetDbContext _dbContext;
    private readonly ILlmProviderFactory _providerFactory;
    private readonly ILogger<PlatformLlmCapabilityHandler> _logger;
    private readonly AgentEmployeeIdentityResolver _employeeIdentityResolver;

    public PlatformLlmCapabilityHandler(
        CSweetDbContext dbContext,
        ILlmProviderFactory providerFactory,
        AgentEmployeeIdentityResolver employeeIdentityResolver,
        ILogger<PlatformLlmCapabilityHandler> logger)
    {
        _dbContext = dbContext;
        _providerFactory = providerFactory;
        _employeeIdentityResolver = employeeIdentityResolver;
        _logger = logger;
    }

    public async IAsyncEnumerable<CapabilityResult> StreamAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (request.Payload.Length > 1_048_576)
        {
            yield return Failure(request.RequestId, "The LLM request exceeds the 1 MB limit.");
            yield break;
        }

        if (!session.Grant.RequestedCapabilities.Contains(PlatformChatCapabilities.ChatStream))
        {
            yield return Failure(request.RequestId,
                $"The installation is not granted {PlatformChatCapabilities.ChatStream}.");
            yield break;
        }

        PlatformChatRequest? input = null;
        var parseFailed = false;
        try
        {
            input = JsonSerializer.Deserialize<PlatformChatRequest>(request.Payload.Span, JsonOptions);
        }
        catch (JsonException)
        {
            parseFailed = true;
        }

        if (parseFailed)
        {
            yield return Failure(request.RequestId, "The LLM request payload is not valid JSON.");
            yield break;
        }

        if (input is null || input.ProviderProfileId == Guid.Empty || input.Messages.Count == 0)
        {
            yield return Failure(request.RequestId, "The LLM request requires a provider and at least one message.");
            yield break;
        }

        if (input.Messages.Count > 128 ||
            input.Messages.Sum(MessageSize) > 262_144 ||
            (input.Tools?.Count ?? 0) > 128)
        {
            yield return Failure(request.RequestId, "The LLM request exceeds the message, text, or tool limit.");
            yield break;
        }

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        var requestToken = requestTimeout.Token;

        var profile = await _dbContext.LlmProviderProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == input.ProviderProfileId && x.IsEnabled, requestToken);
        if (profile is null)
        {
            yield return Failure(request.RequestId, "The selected LLM provider is unavailable.");
            yield break;
        }

        var selectedModel = string.IsNullOrWhiteSpace(input.Model)
            ? profile.DefaultChatModel
            : input.Model.Trim();
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            yield return Failure(request.RequestId, "No model is configured for this LLM request.");
            yield break;
        }

        if (!await IsModelApprovedAsync(
                session,
                input.ProviderProfileId,
                selectedModel,
                profile.DefaultChatModel,
                requestToken))
        {
            yield return Failure(request.RequestId, "The selected model is not approved for this provider profile.");
            yield break;
        }

        var identity = await _employeeIdentityResolver.ResolveAsync(session, requestToken);
        var messages = input.Messages.Select(ToChatMessage).ToList();
        var options = new ChatOptions
        {
            Instructions = identity is null
                ? input.Instructions
                : AgentEmployeeIdentityResolver.ApplyToInstructions(session, identity, input.Instructions),
            Tools = input.Tools?
                .Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(
                    tool.Name,
                    tool.Description,
                    tool.JsonSchema))
                .ToList()
        };
        var responseText = new StringBuilder();
        var responseContents = new List<PlatformChatContent>();
        long? inputTokenCount = null;
        long? outputTokenCount = null;
        string? responseRole = null;

        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        string? providerError = null;
        try
        {
            var chatClient = await _providerFactory.CreateChatClientAsync(
                input.ProviderProfileId,
                selectedModel,
                requestToken);
            updates = chatClient.GetStreamingResponseAsync(
                messages,
                options,
                requestToken).GetAsyncEnumerator(requestToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            providerError = "The platform LLM request timed out.";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Platform LLM request {RequestId} failed for agent {AgentId}.",
                request.RequestId,
                session.AgentId);
            providerError = "The platform LLM provider could not complete the request.";
        }

        if (providerError is not null || updates is null)
        {
            yield return Failure(request.RequestId, providerError ?? "The platform LLM provider could not start the request.");
            yield break;
        }

        await using (updates)
        {
            while (true)
            {
                ChatResponseUpdate? update = null;
                var moved = false;
                try
                {
                    moved = await updates.MoveNextAsync();
                    if (moved)
                    {
                        update = updates.Current;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
                {
                    providerError = "The platform LLM request timed out.";
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Platform LLM stream {RequestId} failed for agent {AgentId}.",
                        request.RequestId,
                        session.AgentId);
                    providerError = "The platform LLM provider could not complete the request.";
                }

                if (providerError is not null)
                {
                    yield return Failure(request.RequestId, providerError);
                    yield break;
                }

                if (!moved || update is null)
                {
                    break;
                }

                var usage = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;
                if (usage is not null)
                {
                    inputTokenCount = usage.InputTokenCount ?? inputTokenCount;
                    outputTokenCount = usage.OutputTokenCount ?? outputTokenCount;
                }
                if (!string.IsNullOrEmpty(update.Text))
                    responseText.Append(update.Text);
                if (update.Role is not null)
                    responseRole = update.Role.ToString();
                responseContents.AddRange(update.Contents
                    .Where(content => content is TextContent or FunctionCallContent or FunctionResultContent)
                    .Select(ToPlatformContent));
            }
        }

        // MCP tools/call has a single result. Aggregate provider updates here so the
        // gateway does not discard every content chunk in favor of an empty terminal
        // marker. The SDK still exposes this through its streaming authoring surface.
        yield return Success(
            request.RequestId,
            new PlatformChatChunk(
                responseText.Length == 0 ? null : responseText.ToString(),
                inputTokenCount,
                outputTokenCount,
                responseRole,
                responseContents),
            sequence: 0,
            hasMore: false);
    }

    private async Task<bool> IsModelApprovedAsync(
        AgentSession session,
        Guid providerProfileId,
        string selectedModel,
        string defaultModel,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(defaultModel) &&
            string.Equals(selectedModel, defaultModel, StringComparison.Ordinal))
        {
            return true;
        }

        if (!Guid.TryParse(session.InstallationId, out var installationId))
        {
            return false;
        }

        var settingsJson = await _dbContext.AgentInstallationConfigurations
            .AsNoTracking()
            .Where(x => x.AgentInstallationId == installationId)
            .Select(x => x.SettingsJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            var root = document.RootElement;
            return root.TryGetProperty("llmProviderId", out var providerElement) &&
                providerElement.ValueKind == JsonValueKind.String &&
                Guid.TryParse(providerElement.GetString(), out var configuredProviderId) &&
                configuredProviderId == providerProfileId &&
                root.TryGetProperty("llmModel", out var modelElement) &&
                modelElement.ValueKind == JsonValueKind.String &&
                string.Equals(modelElement.GetString(), selectedModel, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ChatRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    private static ChatMessage ToChatMessage(PlatformChatMessage message) => new(
        ParseRole(message.Role),
        message.Contents is { Count: > 0 }
            ? message.Contents.Select(ToAiContent).ToList()
            : [new TextContent(message.Text ?? string.Empty)]);

    private static AIContent ToAiContent(PlatformChatContent content) => content.Kind switch
    {
        "text" => new TextContent(content.Text ?? string.Empty),
        "function_call" when !string.IsNullOrWhiteSpace(content.CallId) &&
            !string.IsNullOrWhiteSpace(content.Name) => new FunctionCallContent(
                content.CallId,
                content.Name,
                content.Arguments?.ToDictionary(
                    argument => argument.Key,
                    argument => (object?)argument.Value.Clone(),
                    StringComparer.Ordinal) ?? new Dictionary<string, object?>()),
        "function_result" when !string.IsNullOrWhiteSpace(content.CallId) =>
            new FunctionResultContent(content.CallId, content.Result?.Clone()),
        _ => throw new InvalidOperationException(
            $"The broker request contains unsupported or incomplete '{content.Kind}' content.")
    };

    private static PlatformChatContent ToPlatformContent(AIContent content) => content switch
    {
        TextContent text => new PlatformChatContent("text", Text: text.Text),
        FunctionCallContent call => new PlatformChatContent(
            "function_call",
            CallId: call.CallId,
            Name: call.Name,
            Arguments: call.Arguments?.ToDictionary(
                argument => argument.Key,
                argument => SerializeElement(argument.Value),
                StringComparer.Ordinal)),
        FunctionResultContent result => new PlatformChatContent(
            "function_result",
            CallId: result.CallId,
            Result: SerializeElement(result.Result)),
        _ => throw new NotSupportedException(
            $"Platform LLM responses do not support {content.GetType().Name} content.")
    };

    private static JsonElement SerializeElement(object? value) =>
        value is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), JsonOptions);

    private static int ContentSize(PlatformChatContent content) =>
        (content.Text?.Length ?? 0) +
        (content.CallId?.Length ?? 0) +
        (content.Name?.Length ?? 0) +
        (content.Arguments?.Sum(argument => argument.Key.Length + argument.Value.GetRawText().Length) ?? 0) +
        (content.Result?.GetRawText().Length ?? 0);

    private static int MessageSize(PlatformChatMessage message) =>
        message.Contents is { Count: > 0 }
            ? message.Contents.Sum(ContentSize)
            : message.Text?.Length ?? 0;

    private static CapabilityResult Success(
        string requestId,
        PlatformChatChunk chunk,
        int sequence,
        bool hasMore) => new()
    {
        RequestId = requestId,
        Succeeded = true,
        ContentType = "application/json",
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(chunk, JsonOptions)),
        Sequence = sequence,
        HasMore = hasMore
    };

    private static CapabilityResult Failure(string requestId, string error) => new()
    {
        RequestId = requestId,
        Succeeded = false,
        ContentType = "application/json",
        Error = error,
        HasMore = false
    };
}
