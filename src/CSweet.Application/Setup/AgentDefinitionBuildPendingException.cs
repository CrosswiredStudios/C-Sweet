namespace CSweet.Application.Setup;

public sealed class AgentDefinitionBuildPendingException : Exception
{
    public AgentDefinitionBuildPendingException(Guid definitionId, string message)
        : base(message)
    {
        DefinitionId = definitionId;
    }

    public Guid DefinitionId { get; }
}
