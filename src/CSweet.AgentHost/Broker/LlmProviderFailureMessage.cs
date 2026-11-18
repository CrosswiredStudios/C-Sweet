namespace CSweet.AgentHost.Broker;

internal static class LlmProviderFailureMessage
{
    internal static bool IsTransient(Exception exception) => !IsContextLength(exception) && exception switch
    {
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode is 408 or 429 or >= 500,
        System.ClientModel.ClientResultException client => client.Status is 408 or 429 or >= 500,
        TimeoutException or System.Net.Sockets.SocketException => true,
        System.Net.Http.HttpIOException io => io.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded,
        IOException { InnerException: System.Net.Sockets.SocketException } => true,
        IOException { InnerException: HttpRequestException http } => IsTransient(http),
        _ => exception.Message.Contains("No model loaded", StringComparison.OrdinalIgnoreCase)
    };

    private static bool IsContextLength(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause.Message.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
                cause.Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
                cause.Message.Contains("exceeds the context window", StringComparison.OrdinalIgnoreCase) ||
                cause.Message.Contains("exceeds the model's context", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Return approved explanations, never raw provider bodies, endpoints, credentials or prompts.
    internal static string From(Exception exception)
    {
        if (exception is CSweet.Infrastructure.Llm.ProviderDispatchDeniedException)
            return "The provider configuration or caller authorization changed before dispatch. Refresh the current work and permissions before retrying.";
        if (exception is CSweet.Infrastructure.Llm.LlmResponseContractException contract)
            return contract.Message;
        if (IsContextLength(exception)) return CSweet.Infrastructure.Setup.AgentWorkFailure.ContextLengthMessage;
        if (exception is IOException && IsTransient(exception))
            return "The connection to the model provider was interrupted before the response finished. The incomplete response was not accepted; retry the pending work after the connection recovers.";
        if (exception is TimeoutException)
            return "The model provider did not finish within the request deadline. Retry the pending work; if this repeats, check provider availability or reduce the request size.";
        var text = exception.Message;
        if (text.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase))
            return "The model requires reasoning content to be preserved between tool calls. Update the platform's OpenAI-compatible adapter to retain reasoning history, then retry the blocked ticket.";
        if (text.Contains("No user query found in messages", StringComparison.OrdinalIgnoreCase))
            return "The model requires a user message in this request. Enable the provider's EnsureUserMessage compatibility setting before retrying.";
        if (text.Contains("No model loaded", StringComparison.OrdinalIgnoreCase))
            return "The inference provider has no model loaded. Load the configured model in the provider, or enable its automatic model loading, then retry.";
        return "The platform LLM provider could not complete the request. Review the provider diagnostics before retrying.";
    }

    internal static string CodeFrom(Exception exception) => exception is CSweet.Infrastructure.Llm.ProviderDispatchDeniedException
        ? "llm.dispatch_denied" : exception is CSweet.Infrastructure.Llm.LlmResponseContractException contract
        ? contract.FailureCode : IsContextLength(exception) ? "llm.context_length" : IsTransient(exception) ? "llm.provider_unavailable" : "llm.request_failed";
}
