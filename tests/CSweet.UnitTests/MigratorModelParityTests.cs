using CSweet.Infrastructure;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CSweet.UnitTests;

public sealed class MigratorModelParityTests
{
    [Fact]
    public void Real_startup_services_match_the_migration_snapshot_and_design_time_factory()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Development"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:csweet"] = "Host=127.0.0.1;Database=model_parity_only;Username=postgres;Password=unused"
        });
        builder.AddCSweetInfrastructure();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var expected = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot, designTime: true);
        var current = db.GetService<IDesignTimeModel>().Model;
        var difference = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            expected.GetRelationalModel(), current.GetRelationalModel());
        var sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>()
            .Generate(difference, current).Select(command => command.CommandText));

        Assert.False(db.Database.HasPendingModelChanges(), sql);

        using var design = new DesignTimeDbContextFactory().CreateDbContext([]);
        Assert.Empty(db.GetService<IMigrationsModelDiffer>().GetDifferences(
            design.GetService<IDesignTimeModel>().Model.GetRelationalModel(), current.GetRelationalModel()));
    }
}
