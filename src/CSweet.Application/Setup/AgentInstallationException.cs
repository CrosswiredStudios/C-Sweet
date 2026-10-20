namespace CSweet.Application.Setup;

public class AgentInstallationException : Exception
{
    public AgentInstallationException(string message)
        : base(message)
    {
    }

    public AgentInstallationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
