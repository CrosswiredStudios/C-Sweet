using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Marketplace;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QwenTechnicalDirector_UsesRequestedArtifactCapabilitiesThroughSelectionAndHire(bool omitRequiredCapability)
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Hire approved team roles");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        using var fixtureStream = typeof(HiringServiceTests).Assembly.GetManifestResourceStream("qwen-technical-director-hiring-requirements.json")!;
        using var fixture = await JsonDocument.ParseAsync(fixtureStream);
        var role = await db.ResourceChangeRoles.SingleAsync();
        role.RoleKey = plan.RoleKey = fixture.RootElement.GetProperty("roleKey").GetString()!;
        role.Title = plan.Title = fixture.RootElement.GetProperty("title").GetString()!;
        role.RoleCategoryKey = fixture.RootElement.GetProperty("roleCategoryKey").GetString()!;
        role.RequiredCapabilitiesJson = fixture.RootElement.GetProperty("requiredCapabilities").GetRawText();
        await db.SaveChangesAsync();
        var options = Options.Create(new MarketplaceOptions { FirstPartyAgents = [new()
        {
            Id = Guid.NewGuid(), AgentId = "com.csweet.video-game-technical-director", Name = "Victor Lin",
            RoleKey = "game-technical-director", RoleName = "Video Game Technical Director",
            Capabilities = ["work.execution.run"], RepositoryUrl = TechnicalDirectorCapabilityPreview.Repository
        }] });
        // Exercise real catalog filtering: the old required-capability query removed Victor here.
        var catalog = new AgentCatalogService([new FirstPartyAgentCatalogProvider(options)], NullLogger<AgentCatalogService>.Instance);
        var preview = new TechnicalDirectorCapabilityPreview(db, omitRequiredCapability);
        var definitions = new RecordingDefinitionService();
        var service = new HiringService(db, new RecordingOrganizationUserService(db), new TestAuditEventWriter(),
            preview, agentCatalog: catalog, agentDefinitions: definitions);

        var selected = await service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id);
        if (omitRequiredCapability)
        {
            Assert.Null(selected.Candidate);
            Assert.True(Assert.Single(await service.ListRecommendationsAsync(f.Organization)).NoSuitableAgentFound);
            // Manual review also cannot turn an unsupported capability into a successful hire.
            var candidate = Assert.Single((await catalog.GetAvailableAgentsAsync(f.Organization, new())).Agents);
            var review = await service.PreviewMarketplaceHireAsync(f.Organization, f.Application,
                new(candidate.AgentReference, plan.Title, candidate.Name, f.Owner, "unsupported-review") { RecommendationId = plan.Id });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(f.Organization, review.WorkflowId, f.Application, new("unsupported-confirm")));
            Assert.Contains("no longer satisfies the approved role requirements", error.Message);
            Assert.Equal(0, plan.FulfilledHeadcount);
            Assert.Equal(0, definitions.ImportCount);
            return;
        }

        Assert.Equal("Victor Lin", selected.Candidate?.Name);
        Assert.DoesNotContain("platform.artifact.read.v1", selected.Candidate!.Capabilities);
        Assert.Empty(await db.StaffingActionProposals.ToListAsync()); // Assessment is not a hiring authorization.
        Assert.Equal(0, definitions.ImportCount);
        Assert.Equal("Succeeded", (await service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        Assert.Equal(1, plan.FulfilledHeadcount);
        Assert.Contains("platform.artifact.read.v1", definitions.Request!.GrantedRequestedCapabilities);
        Assert.DoesNotContain("platform.artifact.read.v1", definitions.Request.GrantedCapabilities);
    }

    [Fact]
    public async Task HiringAssessment_PackageUnavailableDoesNotClaimNoSuitableAgent()
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Handle eligible hires");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        (await db.ResourceChangeRoles.SingleAsync()).RequiredCapabilitiesJson = "[\"platform.artifact.read.v1\"]";
        await db.SaveChangesAsync();
        // No import service: discovery succeeds, but the package cannot be verified.
        var candidate = new AvailableAgent("first-party:test", "test", AgentCatalogSource.FirstPartyCatalog, [],
            AgentAvailabilityState.AvailableToInstall, null, "Candidate", "", "C-Sweet", "", [], [], [], 0, "USD",
            null, 0, null, TechnicalDirectorCapabilityPreview.Repository, 1, "");
        var service = new HiringService(db, new RecordingOrganizationUserService(db), new TestAuditEventWriter(),
            agentCatalog: new RecordingAgentCatalog(candidate));
        var result = await service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id);
        Assert.Null(result.Candidate);
        Assert.Contains("Could not verify", result.Rationale);
        var card = Assert.Single(await service.ListRecommendationsAsync(f.Organization));
        Assert.False(card.NoSuitableAgentFound);
        Assert.True(card.AutomaticHiringEnabled);
        Assert.Equal(result.Rationale, card.AutomaticHiringBlockReason);
        Assert.True(CSweet.UI.Services.OverviewAttentionItems.IsActionable(card));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiringAssessment_InstalledCandidateUsesCurrentGrants(bool hasGrant)
    {
        await using var db = CreateDb();
        var f = await AutonomyFixtureAsync(db, source: AgentCatalogSource.Installed);
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        (await db.ResourceChangeRoles.SingleAsync()).RequiredCapabilitiesJson = "[\"platform.artifact.read.v1\"]";
        var installed = await db.AgentInstallations.Include(x => x.Grant).SingleAsync();
        installed.Grant!.RequiredCapabilitiesJson = hasGrant ? "[\"platform.artifact.read.v1\"]" : "[]";
        await db.SaveChangesAsync();

        Assert.Equal(hasGrant, (await f.Service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id)).Candidate is not null);
        Assert.Empty(await db.StaffingActionProposals.ToListAsync());
        Assert.Equal(hasGrant ? "[\"platform.artifact.read.v1\"]" : "[]", installed.Grant.RequiredCapabilitiesJson);
    }

    private sealed class TechnicalDirectorCapabilityPreview(CSweetDbContext db, bool omitRequiredCapability) : IAgentImportPreviewService
    {
        public const string Repository = "https://github.com/CrosswiredStudios/CSweet.Agent.TechnicalDirector.VideoGame";
        public async Task<AgentImportPreviewResponse> PreviewAsync(PreviewAgentImportRequest request, CancellationToken token = default)
        {
            var preview = await new RecordingImportPreview(db, Repository).PreviewAsync(request, token);
            var provided = new[] { "work.execution.run" };
            string[] requested = omitRequiredCapability
                ? ["platform.artifact.create.v1", "platform.artifact.submit.v1"]
                : ["platform.artifact.read.v1", "platform.artifact.create.v1", "platform.artifact.submit.v1"];
            var package = await db.AgentPackageVersions.SingleAsync(token);
            package.ManifestJson = JsonSerializer.Serialize(new
            {
                rolePolicy = new { declaredRoleKeys = new[] { "game-technical-director" } },
                provides = provided.Select(name => new { name }), requires = requested.Select(name => new { name })
            });
            await db.SaveChangesAsync(token);
            return preview with { Capabilities = provided, RequestedCapabilities = requested, ConfigurationFields = [] };
        }
    }
}
