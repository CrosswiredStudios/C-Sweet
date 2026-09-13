using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Application.Compute;

/// <summary>Operator-owned enrollment authority. Missing, disabled or revoked keys return null.</summary>
public interface IComputeNodeTrust
{
    Task<ComputeNodeVerificationKey?> ResolveAsync(Guid organizationId, Guid nodeId, string keyId, CancellationToken token);
}
