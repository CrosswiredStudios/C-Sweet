namespace CSweet.Application.Core;

public interface IOrganizationDataPurgeService
{
    Task PurgeAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public sealed class OrganizationDeletionException : Exception
{
    public OrganizationDeletionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
