using CSweet.Agent.SDK;

namespace CSweet.PersonalAssistant;

public interface IPersonalAssistantReasoner
{
    Task<string> ConverseAsync(ConversationRequest request, AgentRuntimeContext context,
        AgentLlmSelection selection, int outputTokens, CancellationToken cancellationToken);
    Task<CompanyBrief> AnalyzeAsync(RepositoryAnalysisRequest request, AgentRuntimeContext context,
        AgentLlmSelection selection, int outputTokens, CancellationToken cancellationToken);
}
