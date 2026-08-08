using System.Security.Cryptography;
using System.Text;
using CSweet.AI.Providers;
using CSweet.Application.Llm;
using CSweet.Contracts.Llm;
using CSweet.Domain.Setup;
using Microsoft.Extensions.AI;

namespace CSweet.AI.AgentFramework;

public sealed class AgentFrameworkAgentRunner : IAgentRunner
{
    private readonly ILlmProviderFactory _providerFactory;
    private readonly IAgentRunLogWriter _logWriter;

    public AgentFrameworkAgentRunner(
        ILlmProviderFactory providerFactory,
        IAgentRunLogWriter logWriter)
    {
        _providerFactory = providerFactory;
        _logWriter = logWriter;
    }

    public async Task<AgentRunResult> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var logs = new List<AgentRunLogEntry>();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var domainLog = new AgentRunLog
        {
            Id = Guid.NewGuid(),
            AgentKey = request.AgentKey,
            ProviderProfileId = request.ProviderProfileId,
            StartedAt = startedAt,
            Status = "Running",
            PromptHash = ComputePromptHash(request.SystemPrompt + request.UserPrompt),
            InvocationKind = "agent-runner",
            InvocationSequence = 1,
            PromptInstructionCharacters = request.SystemPrompt?.Length ?? 0,
            PromptMessageCharacters = request.UserPrompt.Length +
                request.Context.Sum(item => item.Key.Length + item.Value.Length)
        };

        try
        {
            logs.Add(new AgentRunLogEntry("Info", $"Starting agent run: {request.AgentKey}", DateTimeOffset.UtcNow));

            var chatClient = await _providerFactory.CreateChatClientAsync(
                request.ProviderProfileId, cancellationToken);

            logs.Add(new AgentRunLogEntry("Info", "Provider client created successfully", DateTimeOffset.UtcNow));

            var messages = BuildMessages(request);

            var options = BuildOptions(request.Options);

            logs.Add(new AgentRunLogEntry("Info", "Sending request to LLM provider", DateTimeOffset.UtcNow));

            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);

            stopwatch.Stop();
            var content = response.Text ?? string.Empty;

            domainLog.CompletedAt = DateTimeOffset.UtcNow;
            domainLog.Status = "Completed";
            domainLog.OutputPreview = Truncate(content, 500);
            domainLog.TokenInputCount = ToNullableInt(response.Usage?.InputTokenCount);
            domainLog.TokenOutputCount = ToNullableInt(response.Usage?.OutputTokenCount);
            if (response.Usage?.AdditionalCounts is { Count: > 0 } additionalCounts)
            {
                domainLog.UsageAdditionalCountsJson = System.Text.Json.JsonSerializer.Serialize(additionalCounts);
            }
            domainLog.DurationMs = stopwatch.ElapsedMilliseconds;

            logs.Add(new AgentRunLogEntry("Info", $"Agent run completed successfully in {stopwatch.ElapsedMilliseconds}ms", DateTimeOffset.UtcNow));

            await _logWriter.WriteAsync(domainLog, cancellationToken);

            return new AgentRunResult(
                Succeeded: true,
                Content: content,
                StructuredJson: null,
                FailureMessage: null,
                Logs: logs);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            domainLog.CompletedAt = DateTimeOffset.UtcNow;
            domainLog.Status = "Failed";
            domainLog.FailureMessage = ex.Message;
            domainLog.DurationMs = stopwatch.ElapsedMilliseconds;

            logs.Add(new AgentRunLogEntry("Error", $"Agent run failed: {ex.Message}", DateTimeOffset.UtcNow));

            await _logWriter.WriteAsync(domainLog, cancellationToken);

            return new AgentRunResult(
                Succeeded: false,
                Content: null,
                StructuredJson: null,
                FailureMessage: ex.Message,
                Logs: logs);
        }
    }

    private static List<ChatMessage> BuildMessages(AgentRunRequest request)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.SystemPrompt));
        }

        var userContent = BuildUserContent(request);
        messages.Add(new ChatMessage(ChatRole.User, userContent));

        return messages;
    }

    private static string BuildUserContent(AgentRunRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine(request.UserPrompt);

        if (request.Context.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("--- Context ---");
            foreach (var (key, value) in request.Context)
            {
                var redactedValue = RedactSensitiveValue(key, value);
                builder.AppendLine($"{key}: {redactedValue}");
            }
        }

        return builder.ToString();
    }

    private static readonly HashSet<string> SensitiveKeyPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey",
        "api_key",
        "secret",
        "password",
        "token",
        "bearer",
        "auth"
    };

    private static string RedactSensitiveValue(string key, string value)
    {
        foreach (var pattern in SensitiveKeyPatterns)
        {
            if (key.Contains(pattern))
            {
                return value.Length <= 4 ? "****" : "****" + value[^4..];
            }
        }

        return value;
    }

    private static ChatOptions? BuildOptions(AgentRunOptions options)
    {
        var hasOptions = options.Temperature.HasValue ||
                         options.MaxOutputTokens.HasValue;

        if (!hasOptions && !options.RequireStructuredOutput)
            return null;

        var chatOptions = new ChatOptions();

        if (options.Temperature.HasValue)
            chatOptions.Temperature = (float?)options.Temperature.Value;

        if (options.MaxOutputTokens.HasValue)
            chatOptions.MaxOutputTokens = options.MaxOutputTokens.Value;

        return chatOptions;
    }

    private static string ComputePromptHash(string prompt)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(prompt));
        return Convert.ToBase64String(hash);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static int? ToNullableInt(long? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value > int.MaxValue ? int.MaxValue : (int)value.Value;
    }
}
