using CSweet.Application.Agents;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class AgentGovernancePostgresTests
{
    [GovernancePostgresFact]
    public async Task MigrationSharedPostureAndIndependentHostAdmissionPersistAcrossConnections()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "governance_test_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.MigrateAsync();
            await using var fixture = await TrackedDelegationTests.Fixture.Create(db);
            var installation = fixture.Naomi.AgentInstallationId!.Value;
            db.AgentInstallationGrants.Add(new() { Id = Guid.NewGuid(), AgentInstallationId = installation, GrantRevision = 1 });
            await db.SaveChangesAsync();
            var actor = Guid.NewGuid();
            var postures = new AgentPostureService(new DatabaseAgentPostureStore(db), new PermissivePostureAuthorization(), TimeProvider.System);
            await postures.SetAsync(installation, AgentPosture.Plan, actor);
            await using (var host = new CSweetDbContext(options))
                Assert.Equal(AgentPosture.Plan, (await new DatabaseAgentPostureStore(host).FindAsync(installation))!.Posture);
            await postures.SetAsync(installation, AgentPosture.Manual, actor);

            async Task<CapabilityAdmission> Admit()
            {
                await using var host = new CSweetDbContext(options);
                return await new AgentCapabilityApprovalService(host, TimeProvider.System).AdmitAsync(fixture.Org, installation,
                    "work.item.create.v1", "{}"u8.ToArray(), 1, CapabilityRiskClass.Write);
            }
            var queued = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Admit()));
            var approval = Assert.Single(queued.Select(x => x.ApprovalId).Distinct());
            await new AgentCapabilityApprovalService(db, TimeProvider.System).DecideAsync(fixture.Org, approval, actor, true, true);
            var claimed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Admit()));
            Assert.Single(claimed.Where(x => x.Status == "Execute"));
            await new AgentCapabilityApprovalService(db, TimeProvider.System).CompleteAsync(approval, "[]", true);
            Assert.Equal("[]", (await Admit()).ResultsJson);

            // Independent providers and ledger writers model separate API/host processes.
            var protection = new EphemeralDataProtectionProvider();
            async Task<bool> Reserve()
            {
                var services = new ServiceCollection();
                services.AddDbContext<CSweetDbContext>(builder => builder.UseNpgsql(connection.ConnectionString));
                using var provider = services.BuildServiceProvider();
                var scopes = provider.GetRequiredService<IServiceScopeFactory>();
                var context = new AuditExecutionContextAccessor();
                var writer = new AuditEventWriter(scopes, context, protection);
                var policy = new AgentPolicyService(scopes, writer, context, TimeProvider.System);
                return (await policy.TryAdmitAsync(new("Destructive", TimeSpan.FromHours(1), 3),
                    new("Destructive", installation, fixture.Org.ToString("D"), CapabilityRiskClass.Destructive, DateTimeOffset.UtcNow))).Allowed;
            }
            Assert.Equal(3, (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reserve()))).Count(x => x));
            Assert.NotEmpty(await db.AuditOutbox.ToListAsync());
            Assert.NotEmpty(await db.ApplicationRealtimeOutbox.ToListAsync());
        }
        finally
        {
            Assert.StartsWith("governance_test_", connection.Database);
            await db.Database.EnsureDeletedAsync();
        }
    }
    [GovernancePostgresFact]
    public async Task UpgradePinsUntouchedInstallationsToAutoAndKeepsChosenPostures()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "governance_test_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            var migrator = db.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261103231252_AddCompatiblePluginAccountUpgrades");
            await using var fixture = await TrackedDelegationTests.Fixture.Create(db);
            var rows = await db.AgentInstallations.ToListAsync();
            Assert.True(rows.Count >= 2);
            var chosen = rows[0];
            chosen.Posture = "Plan"; chosen.PostureChangedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            await migrator.MigrateAsync();

            await using var after = new CSweetDbContext(options);
            var postures = await after.AgentInstallations.ToDictionaryAsync(x => x.Id, x => x.Posture);
            Assert.Equal("Plan", postures[chosen.Id]);
            Assert.All(rows.Skip(1), x => Assert.Equal("Auto", postures[x.Id]));
            var store = new DatabaseAgentPostureStore(after);
            Assert.Equal(AgentPosture.Auto, (await store.FindAsync(rows[1].Id))!.Posture);
        }
        finally
        {
            Assert.StartsWith("governance_test_", connection.Database);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class GovernancePostgresFactAttribute : FactAttribute
    {
        public GovernancePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_DELEGATION_TEST_POSTGRES")))
                Skip = "Set CSWEET_DELEGATION_TEST_POSTGRES to a disposable loopback server.";
        }
    }

}
