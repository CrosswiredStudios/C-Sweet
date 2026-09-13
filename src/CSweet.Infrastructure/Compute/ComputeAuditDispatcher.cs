using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Compute;

public sealed class ComputeAuditDispatcher(CSweetDbContext db, IAuditEventWriter writer, TimeProvider clock)
{
    public async Task<int> DispatchAsync(CancellationToken token)
    {
        var rows = await db.ComputeAuditOutbox.Where(x => x.DeliveredAt == null)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        foreach (var row in rows)
        {
            var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(row.RequestJson)
                ?? throw new InvalidOperationException("Compute audit evidence is invalid.");
            if (request.EventId != row.Id) throw new InvalidOperationException("Compute audit identity does not match its receipt.");
            await writer.AppendAsync(request, token);
            row.DeliveredAt = clock.GetUtcNow();
            await db.SaveChangesAsync(token);
        }
        return rows.Count;
    }
}

// Platform-owned bounded recovery scan; agents never poll for audit delivery.
internal sealed class ComputeAuditWorker(IServiceScopeFactory scopes, ILogger<ComputeAuditWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ComputeAuditDispatcher>().DispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Compute audit delivery failed; durable evidence remains pending."); }
        }
    }
}
