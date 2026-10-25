namespace CSweet.Application.Setup;

/// <summary>Deployment-owned storage/processor adapter, never a plugin callback or an agent assertion.</summary>
public interface IPluginAccountDataDeletionParticipant
{
    string Store { get; }
    // Registering a transport without its deployment trust configuration must not unblock consent.
    bool IsConfigured => true;
    /// <summary>Idempotent across crashes/retries. True only after independently confirming all copies for this durable receipt are gone.</summary>
    Task<bool> DeleteAsync(Guid accountId, Guid? organizationId, Guid consumerInstallationId, Guid recordId, CancellationToken cancellationToken);
}
