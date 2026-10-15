namespace CSweet.Application.Core;

public interface IOrganizationDataPurgeService
{
    Task PurgeAsync(Guid organizationId, CancellationToken cancellationToken = default);
}

public sealed class OrganizationDeletionException : Exception
{
    public OrganizationDeletionException(string message, Exception? innerException = null, string errorCode = "deletion_failed")
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
