namespace CSweet.Application.Setup;

public interface IBusinessAgentInstallationCleanup
{
    Task QuiesceAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
