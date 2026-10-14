using System.Text.Json;
using CSweet.Agent.SDK;

namespace CSweet.PersonalAssistant;

/// <summary>The SDK owns transport, configuration hydration, leases and replay protection.</summary>
public sealed class PersonalAssistantAgent(IPersonalAssistantReasoner reasoner) : CSweetAgentBase
{
    public const string ConverseCapability = "personal-assistant.converse.v1";
    public const string AnalyzeCapability = "personal-assistant.analyze-repositories.v1";
    public override string AgentId => "com.csweet.personal-assistant";
    public override string Version => "0.1.0";

    protected override AgentConfigurationBuilder Configure(AgentConfigurationBuilder builder) => builder
        .LlmProvider("llmProviderId", "Default provider", required: true)
        .LlmModel("llmModel", "Assistant model", "llmProviderId", required: true)
        .Number("maxContextWindowTokens", "Maximum context-window tokens", required: true,
            minimum: 4097, defaultValue: 65536, step: 1024)
        .Number("maxOutputTokens", "Maximum output tokens", required: true,
            minimum: 2048, defaultValue: 8192, step: 1024, lessThanFieldKey: "maxContextWindowTokens");

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(AgentCapabilityRequest request,
        AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Capability is not (ConverseCapability or AnalyzeCapability))
            return AgentWorkResult.Failure("This personal-assistant capability is not supported.");
        if (!Guid.TryParse(Settings.GetString("llmProviderId"), out var provider) ||
            string.IsNullOrWhiteSpace(Settings.GetString("llmModel")))
            return AgentWorkResult.Failure("Choose the personal assistant's provider and model in C-Sweet.");
        var selection = new AgentLlmSelection(provider, Settings.GetString("llmModel"));
        try
        {
            await context.ReportProgressAsync(new { stage = "understanding", message = "Preparing your assistant response." }, cancellationToken);
            if (request.Capability == ConverseCapability)
            {
                var input = DeserializePayload<ConversationRequest>(request.Arguments);
                if (input?.Messages is not { Count: > 0 and <= 40 } || input.Messages.Any(x =>
                    x is null || x.Role is not ("user" or "assistant") || x.Content is null || x.Content.Length > 16000) ||
                    input.Messages.Sum(x => (long)x.Content.Length) > 64000 || input.Context?.Length > 64000)
                    return AgentWorkResult.Failure("The conversation request is invalid or too large.");
                return AgentWorkResult.Success(new ConversationResponse(await reasoner.ConverseAsync(
                    input, context, selection, Settings.GetInt32("maxOutputTokens", 8192), cancellationToken)));
            }
            var repositories = DeserializePayload<RepositoryAnalysisRequest>(request.Arguments);
            if (repositories?.Repositories is not { Count: > 0 and <= 50 } ||
                repositories.Repositories.Any(x => x is null || string.IsNullOrWhiteSpace(x.SourceId) || x.Files is null || x.Files.Count > 80 ||
                    x.Files.Any(f => f is null || f.Path is null || f.Content is null)) ||
                repositories.Repositories.Sum(x => x.Files.Sum(f => (long)f.Content.Length)) > 180000)
                return AgentWorkResult.Failure("Choose a bounded set of repository documentation to analyze.");
            return AgentWorkResult.Success(await reasoner.AnalyzeAsync(repositories, context, selection,
                Settings.GetInt32("maxOutputTokens", 8192), cancellationToken));
        }
        catch (JsonException) { return AgentWorkResult.Failure("The assistant request or response was not valid JSON."); }
    }
}
