namespace CSweet.Application.Setup;

/// <summary>Terminates the authenticated guest broker session in the control plane.</summary>
public interface IExecutionBrokerSessionRunner
{
    Task RunAsync(
        Guid assignmentId,
        Stream duplexStream,
        CancellationToken cancellationToken = default);
}
