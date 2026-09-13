using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Application.Compute;

/// <summary>Platform-owned signing key, unavailable to agents and workload code.</summary>
public interface IComputeDispatchSigner
{
    Task<SignedComputeDispatch> SignAsync(ComputeDispatchAuthorization authorization, CancellationToken token);
    Task<ComputeSigningIdentity> GetIdentityAsync(CancellationToken token);
}
