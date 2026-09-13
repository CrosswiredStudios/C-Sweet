namespace CSweet.Application.Compute;

/// <summary>Publish only to authenticated connections for this exact organization/node/provider.</summary>
public interface IComputeProviderWakePublisher
{
    /// <returns>True when the hint was accepted for publication; false when no recipient is available.</returns>
    Task<bool> PublishAsync(Guid organizationId, Guid nodeId, string providerId, Guid eventId, CancellationToken token);
}
