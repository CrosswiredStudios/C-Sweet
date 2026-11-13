namespace CSweet.Application.Setup;

public sealed class AgentImportPreviewException : Exception
{
    public bool IsDependencyFailure { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    /// <summary>GitHub refused the request because the API allowance is used up.</summary>
    public bool IsRateLimited { get; init; }
    public AgentImportPreviewException(string message)
        : base(message)
    {
    }

    public AgentImportPreviewException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
