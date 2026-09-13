using CSweet.Application.Compute;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Compute;

public sealed record ComputeProviderWakeDispatchPass(int Examined, int Published, int Deferred);

/// <summary>At-least-once notification publication. Recipients must discover and claim current work.</summary>
public sealed class ComputeProviderWakeDispatcher(CSweetDbContext db, IComputeProviderWakePublisher publisher, TimeProvider clock)
{
    public async Task<ComputeProviderWakeDispatchPass> DispatchAsync(CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var rows = await db.ComputeProviderWakes.Where(x => x.PublishedAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        var published = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var examined = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) break;
            if (row.OrganizationId == Guid.Empty || row.NodeId == Guid.Empty || row.Id == Guid.Empty || string.IsNullOrWhiteSpace(row.ProviderId))
                throw new InvalidDataException("Provider wake scope is invalid.");
            var accepted = false;
            try { accepted = await publisher.PublishAsync(row.OrganizationId, row.NodeId, row.ProviderId, row.Id, linked.Token); }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is IOException or HttpRequestException or TimeoutException or OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            row.Attempts = Math.Min(row.Attempts, int.MaxValue - 1) + 1; row.Revision = checked(row.Revision + 1);
            if (accepted) { row.PublishedAt = clock.GetUtcNow(); published++; }
            else row.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 9))));
            // A failed save leaves a replayable hint; event IDs remain stable for recipient deduplication.
            await db.SaveChangesAsync(token);
            examined++;
        }
        return new(examined, published, examined - published);
    }
}
