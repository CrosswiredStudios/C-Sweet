using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [HiringAutonomyPostgresFact]
    public async Task HiringAutonomy_PostgresSerializesDuplicateSlotsAndCompetingSpending()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "hiring_autonomy_test_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var setup = new CSweetDbContext(options);
        try
        {
            await setup.Database.EnsureCreatedAsync();
            var f = await AutonomyFixtureAsync(setup, AgentCatalogSource.Installed, requireConfiguration: false, staffed: true);
            await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
                new(HiringSelectionMode.Automatic, AutomaticHiringLevel.ApprovedPackages), 0, "Reuse approved packages");
            var first = await AutonomyPlanAsync(setup, f.Organization, f.Installation, f.Owner);
            var selected = (await f.Service.SelectCandidateAsync(f.Organization, f.Installation, first.Id)).Candidate!;
            var definition = await setup.AgentDefinitions.SingleAsync();

            async Task<DelegatedHireResponse> Submit(Guid plan, AvailableAgent agent)
            {
                await using var db = new CSweetDbContext(options);
                var definitions = new RecordingDefinitionService { DefinitionId = definition.Id };
                definitions.SeedExisting(definition.PackageVersionId);
                var service = new HiringService(db, new RecordingOrganizationUserService(db) { PersistSuccessfulCreate = true }, new TestAuditEventWriter(),
                    agentCatalog: new RecordingAgentCatalog(agent), agentDefinitions: definitions);
                return await service.SubmitDelegatedAsync(f.Organization, f.Installation, plan);
            }
            var duplicate = await Task.WhenAll(Submit(first.Id, selected), Submit(first.Id, selected));
            Assert.All(duplicate, x => Assert.True(x.Status == "Succeeded", x.Message));
            Assert.Equal(1, await setup.HiringRecommendationFulfillments.CountAsync());

            setup.Budgets.Add(new Budget { Id = Guid.NewGuid(), OrganizationId = f.Organization,
                Currency = "USD", ScopeType = BudgetScopeType.Organization, LimitAmount = 100, IsActive = true,
                PeriodStart = DateTimeOffset.UtcNow.AddDays(-1), PeriodEnd = DateTimeOffset.UtcNow.AddDays(30) });
            await setup.SaveChangesAsync();
            await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
                new(HiringSelectionMode.Automatic, AutomaticHiringLevel.WithinLimits, MaximumHireCost: 5, MaximumTotalCost: 5,
                    Currency: "USD", AllowedCapabilities: [], AllowedNetworkAccess: []), 1, "Five total");
            var second = await AutonomyPlanAsync(setup, f.Organization, f.Installation, f.Owner);
            var third = await AutonomyPlanAsync(setup, f.Organization, f.Installation, f.Owner);
            var competing = await Task.WhenAll(Submit(second.Id, selected with { Price = 5 }), Submit(third.Id, selected with { Price = 5 }));
            Assert.Single(competing, x => x.Status == "Succeeded");
            Assert.Single(competing, x => x.Status == "NeedsReview");
            Assert.Equal(2, await setup.HiringRecommendationFulfillments.CountAsync());
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    private sealed class HiringAutonomyPostgresFactAttribute : FactAttribute
    {
        public HiringAutonomyPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES")))
                Skip = "Set CSWEET_HIRING_TEST_POSTGRES to a loopback PostgreSQL test server.";
        }
    }
}
