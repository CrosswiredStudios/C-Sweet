using CSweet.Application.Llm;
using CSweet.Contracts.Llm;

namespace CSweet.AI.AgentFramework;

public sealed class AgentFrameworkWorkflowRunner : IAgentWorkflowRunner
{
    private readonly IAgentRunner _agentRunner;

    public AgentFrameworkWorkflowRunner(IAgentRunner agentRunner)
    {
        _agentRunner = agentRunner;
    }

    public async Task<AgentWorkflowRunResult> RunAsync(
        AgentWorkflowRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var agentRequest = new AgentRunRequest(
            ProviderProfileId: request.ProviderProfileId,
            AgentKey: $"workflow:{request.WorkflowKey}",
            SystemPrompt: request.SystemPrompt,
            UserPrompt: request.UserPrompt,
            Context: request.Context,
            Options: request.Options)
        { OrganizationId = request.OrganizationId, TaskRunId = request.TaskRunId,
            EmployeeId = request.EmployeeId, InvocationKind = "workflow" };

        var result = await _agentRunner.RunAsync(agentRequest, cancellationToken);

        return new AgentWorkflowRunResult(
            Succeeded: result.Succeeded,
            Content: result.Content,
            StructuredJson: result.StructuredJson,
            FailureMessage: result.FailureMessage,
            Logs: result.Logs);
    }
}
