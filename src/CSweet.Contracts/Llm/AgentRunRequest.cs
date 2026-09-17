namespace CSweet.Contracts.Llm;

public sealed record AgentRunRequest(
    Guid ProviderProfileId,
    string AgentKey,
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyDictionary<string, string> Context,
    AgentRunOptions Options)
{
    public Guid? OrganizationId { get; init; }
    public Guid? TaskRunId { get; init; }
    public Guid? EmployeeId { get; init; }
    public Guid? BenchmarkTrialId { get; init; }
    public string InvocationKind { get; init; } = "agent-runner";
}
