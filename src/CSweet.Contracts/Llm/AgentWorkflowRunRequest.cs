namespace CSweet.Contracts.Llm;

public sealed record AgentWorkflowRunRequest(
    string WorkflowKey,
    Guid ProviderProfileId,
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyDictionary<string, string> Context,
    AgentRunOptions Options)
{
    public Guid? OrganizationId { get; init; }
    public Guid? TaskRunId { get; init; }
    public Guid? EmployeeId { get; init; }
}
