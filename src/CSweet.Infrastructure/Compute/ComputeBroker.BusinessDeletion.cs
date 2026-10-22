using CSweet.Compute.Contracts;
using CSweet.Domain.Core;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed partial class ComputeBroker
{
    // Only the owner-authorized Core deletion path can persist this intent. It
    // authorizes destruction of this business's resources independently of the
    // employee who originally provisioned them, including disabled/removed agents.
    // Re-read at admission and dispatch; caller keys are never authority.
    internal async Task<ComputeActionAuthorization?> BusinessDeletionAuthorityAsync(
        Guid organizationId, string action, CancellationToken token)
    {
        if (action != InfrastructureActions.Destroy || !await db.CoreOrganizations.AsNoTracking().AnyAsync(x =>
                x.Id == organizationId && (x.Status == OrganizationStatus.Deleting || x.Status == OrganizationStatus.DeletionFailed), token))
            return null;

        // The evidence identity is the persisted business deletion intent, not an
        // employee grant. The provider receives the usual short-lived, signed,
        // exact-environment/generation destroy packet; no new agent grants exist.
        return new(organizationId, 1, InfrastructureActions.Destroy, DateTimeOffset.MaxValue);
    }
}
