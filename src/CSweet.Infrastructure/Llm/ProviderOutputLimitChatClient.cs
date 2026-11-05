using System.ClientModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Llm;

// Saved budgets are ceilings, not a reservation. Try them unchanged first. Some compatible
// servers reject prompt + maximum output before generation; only that rejection permits a
// single retry using the server's exact counts. Never estimate tokens or remove input.
internal sealed partial class ProviderOutputLimitChatClient(IChatClient inner, ILogger? logger = null)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        try
        {
            return await InnerClient.GetResponseAsync(history, options, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (ClientResultException error) when (!cancellationToken.IsCancellationRequested &&
            TryGetRetryOptions(error, options, out var retry))
        {
            LogAdjustment(options!, retry);
            cancellationToken.ThrowIfCancellationRequested();
            return await InnerClient.GetResponseAsync(history, retry, cancellationToken);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var currentOptions = options;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = false;
            Exception? failure = null;
            // Dispose a rejected stream before starting the next HTTP request.
            await using (var updates = InnerClient.GetStreamingResponseAsync(history, currentOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    bool moved;
                    try { moved = await updates.MoveNextAsync(); }
                    catch (Exception error) { failure = error; break; }
                    if (!moved) break;
                    started = true; // Even metadata, reasoning or usage means generation may have started.
                    yield return updates.Current;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is null) yield break;
            if (attempt == 0 && !started && !cancellationToken.IsCancellationRequested &&
                failure is ClientResultException rejection && TryGetRetryOptions(rejection, options, out var retry))
            {
                LogAdjustment(options!, retry);
                currentOptions = retry;
                continue;
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void LogAdjustment(ChatOptions original, ChatOptions retry) => logger?.LogInformation(
        "Provider rejected the output ceiling before generation; retrying once with {AppliedOutputTokens} output tokens instead of {RequestedOutputTokens}. Saved limits and input are unchanged.",
        retry.MaxOutputTokens, original.MaxOutputTokens);

    private static bool TryGetRetryOptions(ClientResultException error, ChatOptions? options, out ChatOptions retry)
    {
        retry = null!;
        if (error.Status != 400 || options?.MaxOutputTokens is not > 0) return false;
        // Read only the provider's structured error message, never prompt text or model output.
        var content = error.GetRawResponse()?.Content;
        if (content is null || content.ToMemory().Length > 32768) return false;
        string message;
        try
        {
            using var body = JsonDocument.Parse(content.ToMemory());
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                !body.RootElement.TryGetProperty("error", out var detail) || detail.ValueKind != JsonValueKind.Object ||
                !detail.TryGetProperty("message", out var text) || text.ValueKind != JsonValueKind.String) return false;
            message = text.GetString()!;
        }
        catch (JsonException) { return false; }

        var match = SuggestedLimit().Match(message);
        if (!match.Success) match = VllmLimit().Match(message);
        if (!match.Success) match = LegacyVllmLimit().Match(message);
        if (!match.Success || !ReadCount(match, "input", out var input) ||
            !ReadCount(match, "context", out var context) || !ReadCount(match, "output", out var requested) ||
            requested != options.MaxOutputTokens || input < 0 || context <= input || (long)input + requested <= context)
            return false;

        var allowed = context - input;
        if (match.Groups["allowed"].Success)
        {
            if (!ReadCount(match, "allowed", out var suggested) || suggested <= 0 || suggested > allowed) return false;
            allowed = suggested; // Preserve any extra headroom required by this server.
        }
        if (allowed >= requested) return false;
        retry = options.Clone();
        retry.MaxOutputTokens = allowed;
        return true;
    }

    private static bool ReadCount(Match match, string group, out int value) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"^request exceeds the context window: prompt \((?<input>[0-9]+) tokens\) \+ max tokens \((?<output>[0-9]+)\) exceeds the context \((?<context>[0-9]+)\); requests are never truncated\. Send a smaller max_tokens \(at most (?<allowed>[0-9]+) here\)", RegexOptions.CultureInvariant)]
    private static partial Regex SuggestedLimit();

    [GeneratedRegex(@"^This model's maximum context length is (?<context>[0-9]+) tokens\. However, you requested (?<output>[0-9]+) output tokens and your prompt contains (?<input>[0-9]+) input tokens", RegexOptions.CultureInvariant)]
    private static partial Regex VllmLimit();

    [GeneratedRegex(@"^'max_tokens' or 'max_completion_tokens' is too large: (?<output>[0-9]+)\. This model's maximum context length is (?<context>[0-9]+) tokens and your request has (?<input>[0-9]+) input tokens", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVllmLimit();
}
