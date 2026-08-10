using CSweet.Application.Setup;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.Infrastructure.Persistence;

public static class CSweetDatabaseInitializer
{
    public static async Task EnsureDatabaseReadyAsync(
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();

        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        // The API, AgentHost, and workers share protected durable payloads through
        // PostgreSQL. Seed the shared key ring in the migrator before those processes
        // start so they cannot each create and cache a different first key.
        EnsureDataProtectionKeyRing(scope.ServiceProvider);

        var setupService = scope.ServiceProvider.GetRequiredService<ISetupService>();
        await setupService.EnsureSeededAsync(cancellationToken);
    }

    internal static void EnsureDataProtectionKeyRing(
        IServiceProvider serviceProvider,
        DateTimeOffset? now = null)
    {
        var keyManager = serviceProvider.GetRequiredService<IKeyManager>();
        if (keyManager.GetAllKeys().Count > 0)
        {
            return;
        }

        var activationDate = now ?? DateTimeOffset.UtcNow;
        keyManager.CreateNewKey(activationDate, activationDate.AddDays(90));
    }
}
