using CSweet.Application.Compute;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeFailedProvisionCleanup(CSweetDbContext db, ComputeBroker broker)
{
    public async Task RunOnceAsync(CancellationToken token)
    {
        var failed = await db.ComputeEnvironments.AsNoTracking().Where(x =>
            x.Generation == 1 && x.State == ComputeLifecycleState.Failed &&
            x.Persistence == ComputePersistence.Ephemeral && x.DesiredState != ComputeDesiredState.Destroyed &&
            x.TeardownConfirmedAt == null).OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        foreach (var environment in failed)
        {
            try
            {
                // The broker rechecks the current actor, destroy grant and generation, and
                // atomically records the operation, audit and durable provider/agent wakes.
                await broker.ChangeLifecycleAsync(environment.OrganizationId, environment.InstallationId,
                    new(environment.Id, environment.Generation, InfrastructureActions.Destroy,
                        $"failed-provision-cleanup:{environment.Id:D}"), token);
            }
            catch (UnauthorizedAccessException) { db.ChangeTracker.Clear(); } // Local lease enforcement remains active.
            catch (InvalidOperationException) { db.ChangeTracker.Clear(); } // Concurrent lifecycle change; rediscover next pass.
        }
    }
}
