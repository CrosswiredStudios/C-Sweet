using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class AuditOutboxDispatcher(CSweetDbContext db, IAuditEventWriter writer, TimeProvider clock)
{
    public async Task<int> DispatchAsync(CancellationToken token)
    {
        var rows = await db.AuditOutbox.Where(x => x.DeliveredAt == null && (x.NextAttemptAt == null || x.NextAttemptAt <= clock.GetUtcNow()))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(100).ToListAsync(token);
        foreach (var row in rows)
        {
            try
            {
                var json = row.ProtectedRequest is null ? row.RequestJson :
                    System.Text.Encoding.UTF8.GetString((db.AuditProtection ?? throw new InvalidOperationException("Audit protection is unavailable."))
                        .CreateProtector("CSweet.AuditOutbox.v1").Unprotect(row.ProtectedRequest));
                var request = JsonSerializer.Deserialize<AuditEventWriteRequest>(json)
                    ?? throw new InvalidOperationException("Audit evidence is invalid.");
                if (request.EventId != row.Id) throw new InvalidOperationException("Audit identity does not match its receipt.");
                await writer.AppendAsync(request, token);
                row.DeliveredAt = clock.GetUtcNow();
                row.LastError = null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                row.Attempts++;
                row.LastError = error.GetType().Name;
                row.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(row.Attempts, 8))));
            }
            await db.SaveChangesAsync(token);
        }
        return rows.Count;
    }
}

// Platform-owned bounded recovery scan; agents never poll for audit delivery.
internal sealed class AuditOutboxWorker(IServiceScopeFactory scopes, ILogger<AuditOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AuditOutboxDispatcher>().DispatchAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<CSweet.Infrastructure.Setup.AuditHistoryImporter>().ImportBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Audit delivery failed; durable evidence remains pending."); }
        }
    }
}
