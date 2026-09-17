using System.Text.Json;
using CSweet.Contracts.Analytics;
using CSweet.Domain.Analytics;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.IntegrationTests;

public sealed class BenchmarkPostgresFactAttribute : FactAttribute
{
    public BenchmarkPostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CSWEET_EFFICIENCY_TEST_POSTGRES")))
            Skip = "Set CSWEET_EFFICIENCY_TEST_POSTGRES to an isolated efficiency_validation database.";
    }
}

public sealed class BenchmarkPostgresTests
{
    [BenchmarkPostgresFact]
    public async Task Migration_Aggregates_ConcurrentLaunch_AndSequentialRecovery()
    {
        var connection = Environment.GetEnvironmentVariable("CSWEET_EFFICIENCY_TEST_POSTGRES")!;
        var isolated = new NpgsqlConnectionStringBuilder(connection);
        Assert.StartsWith("efficiency_validation", isolated.Database);
        isolated.Database = "efficiency_validation_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(isolated.ConnectionString).Options;
        var now = DateTimeOffset.UtcNow;
        BenchmarkService Service(CSweetDbContext context, DateTimeOffset time) => new(context, new Clock(time), null!, null!, null!, null!, null!);
        var user = Guid.NewGuid(); var definitionId = Guid.NewGuid(); var provider = Guid.NewGuid();
        await using (var db = new CSweetDbContext(options))
        {
            await db.Database.MigrateAsync();
            var model = new BenchmarkModel(provider, "test");
            var blueprint = new BenchmarkBlueprint("Postgres test", "Deliver", [new("lead", "Lead", Guid.NewGuid(), Guid.NewGuid())],
                [new("A", model, new Dictionary<string, BenchmarkModel>()), new("B", model, new Dictionary<string, BenchmarkModel>())],
                [new("product", "Product", "ArtifactExists")], []);
            db.BenchmarkDefinitions.Add(new() { Id = definitionId, FamilyId = Guid.NewGuid(), Version = 1, Name = blueprint.Name,
                BlueprintJson = JsonSerializer.Serialize(blueprint, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CreatedBy = user, CreatedAt = now });
            db.LlmProviderProfiles.Add(new() { Id = provider, Name = "Isolated fake provider", DefaultChatModel = "test" });
            await db.SaveChangesAsync();
        }
        var launchKey = Guid.NewGuid().ToString();
        async Task<Guid> Launch()
        {
            await using var db = new CSweetDbContext(options);
            return (await Service(db, now).LaunchAsync(new(definitionId, launchKey, 20), user)).Id;
        }
        var campaigns = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Launch()));
        Assert.Single(campaigns.Distinct());
        await using var verify = new CSweetDbContext(options);
        var campaign = campaigns[0];
        var trials = await verify.BenchmarkTrials.Where(x => x.CampaignId == campaign).OrderBy(x => x.ExecutionOrder).ToListAsync();
        Assert.Equal(40, trials.Count);
        var first = trials[0]; first.Status = "Running"; first.StartedAt = now; first.NextRecoveryAt = now.AddMinutes(1);
        verify.AgentRunLogs.Add(new AgentRunLog { Id = Guid.NewGuid(), BenchmarkTrialId = first.Id,
            ProviderProfileId = provider, AgentKey = "test", MeasurementKind = "ProviderAttempt", ProviderStartedAt = now,
            StartedAt = now, Status = "Completed", ReportedInputTokens = 3_000_000_000, ReportedOutputTokens = 7 });
        await verify.SaveChangesAsync();
        var result = await Service(verify, now).GetCampaignAsync(campaign);
        Assert.Equal(3_000_000_007, result!.Trials.Single(x => x.Id == first.Id).DeliveryUsage.TotalTokens);
        // More than the dispatcher batch of older, ineligible pending trials must not starve active work.
        Assert.True(await Service(verify, now.AddMinutes(2)).AdvanceAsync());
        Assert.Equal(now.AddMinutes(3), first.NextRecoveryAt);
        Assert.Equal(39, await verify.BenchmarkTrials.CountAsync(x => x.CampaignId == campaign && x.Status == "Pending"));
    }

    private sealed class Clock(DateTimeOffset time) : TimeProvider { public override DateTimeOffset GetUtcNow() => time; }
}
