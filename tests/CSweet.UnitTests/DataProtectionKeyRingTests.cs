using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class DataProtectionKeyRingTests
{
    [Fact]
    public void Bootstrap_AllowsIndependentHostsToReadProtectedDurablePayloads()
    {
        var databaseName = $"data-protection-{Guid.NewGuid():N}";
        var databaseRoot = new InMemoryDatabaseRoot();
        using var migrator = BuildHost(databaseName, databaseRoot);
        using var producer = BuildHost(databaseName, databaseRoot);
        using var consumer = BuildHost(databaseName, databaseRoot);

        using (var scope = migrator.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CSweetDbContext>().Database.EnsureCreated();
            CSweetDatabaseInitializer.EnsureDataProtectionKeyRing(
                scope.ServiceProvider,
                new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        }

        var producerProtector = producer.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("CSweet.AgentWorkInbox.Payload.v1");
        var consumerProtector = consumer.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("CSweet.AgentWorkInbox.Payload.v1");
        var protectedPayload = producerProtector.Protect("durable chat payload");

        Assert.Equal("durable chat payload", consumerProtector.Unprotect(protectedPayload));
        using var verificationScope = consumer.CreateScope();
        Assert.Single(verificationScope.ServiceProvider
            .GetRequiredService<CSweetDbContext>()
            .DataProtectionKeys);
    }

    private static ServiceProvider BuildHost(string databaseName, InMemoryDatabaseRoot databaseRoot)
    {
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, databaseRoot));
        services.AddDataProtection()
            .SetApplicationName("CSweet")
            .PersistKeysToDbContext<CSweetDbContext>();
        return services.BuildServiceProvider();
    }
}
