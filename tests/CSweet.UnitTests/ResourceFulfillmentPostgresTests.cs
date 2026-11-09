using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.UnitTests;

public sealed class ResourceFulfillmentPostgresTests
{
    [ResourcePostgresFact]
    public async Task Concurrent_request_approval_and_worker_replays_have_one_durable_effect()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "resource_fulfillment_test_" + Guid.NewGuid().ToString("N"); connection.Pooling = false;
        CSweetDbContext Open() => new(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options);
        await using var seed = Open();
        try
        {
            await seed.Database.EnsureCreatedAsync();
            var organization = Guid.NewGuid(); var installation = Guid.NewGuid(); var project = Guid.NewGuid();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "resource-owner" };
            var manager = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization, ApplicationUserId = user.Id,
                DisplayName = "Owner", IsActive = true, PermissionLevel = OrganizationPermissionLevel.Owner, EmployeeType = EmployeeType.Human };
            var requester = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
                DisplayName = "Technical Director", IsActive = true, EmployeeType = EmployeeType.Agent, ReportsToOrganizationUserId = manager.Id };
            var source = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/resource-agent" };
            var package = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = "test.resources",
                Version = "1.0.0", ManifestJson = "{}" };
            seed.AddRange(user, new Organization { Id = organization, Name = "Resource test" }, source, package,
                new AgentInstallation { Id = installation, BusinessId = organization.ToString("D"), PackageVersionId = package.Id,
                    IsEnabled = true, Scope = PluginInstallationScope.Organization, SetupState = PluginSetupState.Ready, RevisionStatus = PluginRevisionStatus.Active },
                manager, requester, new Workstream { Id = project, OrganizationId = organization, Name = "Game", Status = WorkstreamStatus.Approved,
                    AccountableManagerOrganizationUserId = requester.Id });
            await seed.SaveChangesAsync();
            var obsolete = new WorkstreamDecisionRecord { Id = Guid.NewGuid(), OrganizationId = organization, WorkstreamId = project,
                RequestedByOrganizationUserId = requester.Id, RequestedByInstallationId = installation,
                TypeKey = "video-game.toolchain-selection.v1", AuthorityRuleKey = "unsupported-or-uncertified-toolchain",
                Status = "Pending", TypeDataJson = "{\"recipeKey\":\"phaser.web-2d.v1\"}", IdempotencyKey = "old-manual-question" };
            seed.Add(obsolete); await seed.SaveChangesAsync();
            var handler = new ResourceFulfillmentTests.Handler();
            var input = new ResourceRequest(project, ResourceCapabilities.BuildToolchain, "phaser.web-2d.v1", "Build the approved game", "same-request");
            async Task<ResourceRequestState> Request()
            {
                await using var db = Open(); return await new ResourceFulfillmentService(db, [handler], TimeProvider.System).RequestAsync(organization, installation, input, default);
            }
            var requested = await Task.WhenAll(Request(), Request());
            Assert.Equal(requested[0].Id, requested[1].Id);
            await seed.Entry(obsolete).ReloadAsync();
            Assert.Equal("Cancelled", obsolete.Status); Assert.Contains(requested[0].Id.ToString("D"), obsolete.Rationale);
            await using (var discovery = Open())
            {
                var scoped = await new ResourceFulfillmentService(discovery, [handler], TimeProvider.System)
                    .ReadAsync(organization, installation, new(WorkstreamId: project), default);
                Assert.Equal(requested[0].Id, Assert.Single(scoped).Id);
            }
            await using (var preparation = Open())
                await new ResourceFulfillmentService(preparation, [handler], TimeProvider.System).ProcessNextAsync(default);
            var prepared = await new ResourceFulfillmentService(seed, [handler], TimeProvider.System).GetAsync(organization, requested[0].Id, default);
            Assert.Equal("PendingApproval", prepared.Status);
            var decision = new ResourceDecision(prepared.Id, prepared.Revision, prepared.PlanHash, "Approve", "Required project capacity", "same-decision");
            async Task Decide()
            {
                await using var db = Open(); await new ResourceFulfillmentService(db, [handler], TimeProvider.System).DecideAsync(manager, decision, default);
            }
            await Task.WhenAll(Decide(), Decide());
            async Task Execute()
            {
                await using var db = Open(); await new ResourceFulfillmentService(db, [handler], TimeProvider.System).ProcessNextAsync(default);
            }
            await Task.WhenAll(Execute(), Execute());
            Assert.Equal(1, handler.Installations);
            Assert.Single(await seed.ActionProposals.AsNoTracking().ToListAsync());
            var state = await new ResourceFulfillmentService(seed, [handler], TimeProvider.System).GetAsync(organization, requested[0].Id, default);
            Assert.Equal("Installing", state.Status);
            Assert.Equal(4, state.Revision);
            Assert.Equal(4, await seed.AgentPlatformEventOutbox.CountAsync());
        }
        finally
        {
            Assert.StartsWith("resource_fulfillment_test_", connection.Database);
            await seed.Database.EnsureDeletedAsync();
        }
    }
    private sealed class ResourcePostgresFactAttribute : FactAttribute
    {
        public ResourcePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_COMPUTE_TEST_DATABASE")))
                Skip = "Requires loopback PostgreSQL; creates and deletes its own isolated database.";
        }
    }
}
