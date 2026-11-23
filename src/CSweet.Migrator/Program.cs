using CSweet.Application.Setup;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.AddServiceDefaults();
builder.AddCSweetInfrastructure();

if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddHostedService<InstalledMigration>();
    using var serviceHost = builder.Build();
    await serviceHost.RunAsync();
    return Environment.ExitCode;
}

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var logger = scope.ServiceProvider
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("CSweet.Migrator");

try
{
    logger.LogInformation("Preparing C-Sweet database and setup seed data.");
    await CSweetDatabaseInitializer.EnsureDatabaseReadyAsync(host.Services);

    logger.LogInformation("C-Sweet database migration and setup seeding completed.");
    return 0;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "C-Sweet database migration failed.");
    return 1;
}

// The installer starts this demand-start virtual account after provisioning its
// own configuration. It has no listener and accepts no caller-supplied paths.
internal sealed class InstalledMigration(IServiceProvider services, IHostApplicationLifetime lifetime,
    ILogger<InstalledMigration> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "CSweet", "Provisioning", "Migration");
        try
        {
            await CSweetDatabaseInitializer.EnsureDatabaseReadyAsync(services, stoppingToken);
            if (File.Exists(Path.Combine(root, "enroll-office")))
            {
                await using var scope = services.CreateAsyncScope();
                var fleet = scope.ServiceProvider.GetRequiredService<IExecutionFleetService>();
                var result = await fleet.CreateEnrollmentAsync(stoppingToken);
                if (result.Enrollment?.EnrollmentToken is not { Length: > 0 } token)
                    throw new InvalidOperationException("The first Office enrollment could not be issued.");
                await PublishAsync("enrollment.secret", token);
                if (File.Exists(Path.Combine(root, "combined-enrollment.id")))
                    await PublishAsync("combined-enrollment.id", result.Enrollment.Id.ToString("D"));
                if (File.Exists(Path.Combine(root, "compute-business.id")))
                {
                    var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
                    await using var tx = await db.Database.BeginTransactionAsync(stoppingToken);
                    await CSweet.Infrastructure.Setup.FirstOfficeBusiness.LockAsync(db, stoppingToken);
                    var businesses = await db.CoreOrganizations.AsNoTracking()
                        .Where(x => x.Status == CSweet.Domain.Core.OrganizationStatus.Active ||
                            x.Status == CSweet.Domain.Core.OrganizationStatus.Draft)
                        .Select(x => x.Id).Take(2).ToListAsync(stoppingToken);
                    // A shared Headquarters needs an explicitly scoped deployment policy.
                    Guid? business = businesses.Count == 1 ? businesses[0] :
                        await CSweet.Infrastructure.Setup.FirstOfficeBusiness.ReserveAsync(db, null, stoppingToken);
                    await db.SaveChangesAsync(stoppingToken);
                    await tx.CommitAsync(stoppingToken);
                    if (business.HasValue) await PublishAsync("compute-business.id", business.Value.ToString("D"));
                }
            }
            await PublishAsync("completed", "1");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Installed Headquarters migration failed.");
            Environment.ExitCode = 1;
        }
        finally { lifetime.StopApplication(); }

        async Task PublishAsync(string name, string value)
        {
            var destination = Path.Combine(root, name);
            // The elevated installer owns the directory and preallocates each
            // file. This account may write its data, never replace a file/path.
            await using var stream = new FileInfo(destination).Create(FileMode.Open,
                System.Security.AccessControl.FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, null);
            stream.SetLength(0);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value), stoppingToken);
            stream.Flush(flushToDisk: true);
        }
    }
}
