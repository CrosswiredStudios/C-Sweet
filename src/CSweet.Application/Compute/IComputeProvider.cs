using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Application.Compute;

/// <summary>
/// Infrastructure-owned boundary, never an agent tool or host-shell API. Provider side effects
/// require a durably recorded operation and current authorization. Unknown outcomes keep reservations.
/// Docker, networking, persistent storage and bootstrap are not implicit provision side effects.
/// </summary>
public interface IComputeProvider
{
    Task<ComputeProviderCapabilities> GetCapabilitiesAsync(CancellationToken token);
    Task<ComputeProviderObservation> ProvisionAsync(ComputeProviderOperation operation,
        ComputeSpecification specification, ComputeTemplate template, CancellationToken token);
    Task<ComputeProviderObservation> ObserveAsync(ComputeProviderOperation operation, CancellationToken token);
    Task<ComputeProviderObservation> StartAsync(ComputeProviderOperation operation, CancellationToken token);
    Task<ComputeProviderObservation> StopAsync(ComputeProviderOperation operation, CancellationToken token);
    Task<ComputeProviderObservation> RestartAsync(ComputeProviderOperation operation, CancellationToken token);
    Task<ComputeProviderObservation> DestroyAsync(ComputeProviderOperation operation, CancellationToken token);
}
