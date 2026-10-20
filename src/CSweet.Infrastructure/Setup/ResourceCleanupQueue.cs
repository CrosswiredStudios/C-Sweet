using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public static class ResourceCleanupQueue
{
    public const string Runtime = "installation-runtime";
    public const string BusinessStorage = "business-storage";
    public const string LocalCompute = "local-compute-storage";

    // The caller commits this outbox row in the same transaction as deactivation.
    public static async Task<ResourceCleanupRequest> EnqueueAsync(CSweetDbContext db, string kind,
        Guid resource, Guid business, DateTimeOffset now, CancellationToken token)
    {
        var request = db.ResourceCleanupRequests.Local.FirstOrDefault(x => x.Kind == kind && x.ResourceId == resource)
            ?? await db.ResourceCleanupRequests.SingleOrDefaultAsync(x => x.Kind == kind && x.ResourceId == resource, token);
        if (request is null)
        {
            request = new() { Id = Guid.NewGuid(), Kind = kind, ResourceId = resource, BusinessId = business };
            db.ResourceCleanupRequests.Add(request);
        }
        if (request.CompletedAt is not null || request.RequestedAt == default)
        {
            request.RequestedAt = now;
            request.AvailableAt = now;
            request.CompletedAt = null;
            request.LastError = null;
        }
        return request;
    }
}
