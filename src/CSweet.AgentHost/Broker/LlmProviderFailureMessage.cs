namespace CSweet.AgentHost.Broker;

internal static class LlmProviderFailureMessage
{
    // Return approved explanations, never raw provider bodies, endpoints, credentials or prompts.
    internal static string From(Exception exception)
    {
        var text = exception.Message;
        if (text.Contains("No model loaded", StringComparison.OrdinalIgnoreCase))
            return "The inference provider has no model loaded. Load the configured model in the provider, or enable its automatic model loading, then retry.";
        if (text.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("context window", StringComparison.OrdinalIgnoreCase))
            return "The request exceeds the model's context capacity. Reduce the supplied context or configure a model with sufficient capacity, then retry.";
        return "The platform LLM provider could not complete the request. Review the provider diagnostics before retrying.";
    }
}
