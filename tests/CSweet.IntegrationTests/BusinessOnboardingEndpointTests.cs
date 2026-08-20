using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.BusinessOnboarding;
using CSweet.Contracts.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public class BusinessOnboardingEndpointTests
{
    [Fact]
    public async Task GetBriefingSettings_WithoutActiveChief_ReturnsNoContent()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/core/organizations/{Guid.NewGuid():D}/executive-briefings/settings");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CompleteOnboarding_IsBlockedUntilFirstRunSetupIsComplete()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/business-onboarding/complete",
            CreateRequest(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CompleteOnboarding_ReturnsOrganizationAndCreatesInitialGraph()
    {
        await using var factory = CreateFactory();
        await MarkSetupCompleteAsync(factory);
        var client = factory.CreateClient();
        var definitionId = await SeedChiefDefinitionAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/business-onboarding/complete",
            CreateRequest(definitionId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<CompleteBusinessOnboardingResponse>();

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.OrganizationId);
        Assert.Equal(6, result.CreatedRoleCount);
        Assert.Equal(0, result.CreatedTaskCount);
        Assert.True(result.OrganizationActivated);
        Assert.StartsWith($"/organizations/{result.OrganizationId}/communications/", result.NextRoute);
        Assert.NotNull(result.ChiefOrganizationUserId);

        var organization = await client.GetFromJsonAsync<OrganizationResponse>($"/api/organizations/{result.OrganizationId}");
        var roles = await client.GetFromJsonAsync<IReadOnlyList<RoleResponse>>($"/api/organizations/{result.OrganizationId}/roles");
        var tasks = await client.GetFromJsonAsync<IReadOnlyList<WorkTaskResponse>>($"/api/organizations/{result.OrganizationId}/tasks");
        var workers = await client.GetFromJsonAsync<IReadOnlyList<WorkerResponse>>($"/api/organizations/{result.OrganizationId}/workers");

        Assert.NotNull(organization);
        Assert.Equal("Example Co", organization.Name);
        Assert.NotNull(roles);
        Assert.Equal(6, roles.Count);
        Assert.NotNull(tasks);
        Assert.Empty(tasks);
        Assert.NotNull(workers);
        Assert.Contains(workers, x => x.Id == result.DefaultWorkerId && x.Name == "Local Strategy Agent");

        var activated = await client.GetFromJsonAsync<OrganizationResponse>($"/api/organizations/{result.OrganizationId}");
        Assert.Equal("Active", activated?.Status);
        Assert.False(activated?.NeedsChiefSetup);

        var settings = await client.GetFromJsonAsync<ExecutiveBriefingSettingsResponse>(
            $"/api/core/organizations/{result.OrganizationId}/executive-briefings/settings");
        Assert.NotNull(settings);
        Assert.True(settings.StartupEnabled);
        Assert.Equal("Weekdays", settings.Cadence);
        var manualResponse = await client.PostAsync(
            $"/api/core/organizations/{result.OrganizationId}/executive-briefings/run", null);
        Assert.Equal(HttpStatusCode.Accepted, manualResponse.StatusCode);
        var manual = await manualResponse.Content.ReadFromJsonAsync<ExecutiveBriefingActionResponse>();
        Assert.NotNull(manual?.RequestId);
        var history = await client.GetFromJsonAsync<IReadOnlyList<ExecutiveBriefingHistoryItem>>(
            $"/api/core/organizations/{result.OrganizationId}/executive-briefings/history");
        Assert.Contains(history!, x => x.RequestId == manual.RequestId && x.TriggerType == "Manual");
    }

    [Fact]
    public async Task CompleteOnboarding_RequiresChiefAgentDefinition()
    {
        await using var factory = CreateFactory();
        await MarkSetupCompleteAsync(factory);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/business-onboarding/complete",
            CreateRequest(Guid.Empty));
        var result = await response.Content.ReadFromJsonAsync<BusinessOnboardingActionResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal("chief_agent_required", result.ErrorCode);
    }

    private static CompleteBusinessOnboardingRequest CreateRequest(Guid chiefDefinitionId) =>
        new(
            "Example Co",
            "Software",
            "Launch a paid MVP that helps small teams plan their work.",
            chiefDefinitionId);

    private static async Task MarkSetupCompleteAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        dbContext.SystemConfigurations.Add(new SystemConfiguration
        {
            Id = Guid.NewGuid(),
            IsFirstRunComplete = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> SeedChiefDefinitionAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = Guid.NewGuid(), AgentId = "example.chief", AgentName = "Example Chief",
            Version = "1.0.0", PluginKind = PluginKind.Agent,
            ManifestJson = """{"kind":"agent","provides":[{"name":"assistant.converse.v1"}]}""",
            Status = AgentPackageVersionStatus.Built,
            PackageDigest = new string('f', 64),
            ArtifactSignature = "test-signature",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(), PackageSourceId = package.PackageSourceId, AgentId = package.AgentId,
            PackageVersionId = package.Id, PackageVersion = package, Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true, DefaultActivationMode = ActivationMode.OnDemand,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(), AgentDefinitionId = definition.Id, SchemaVersion = "1", SettingsJson = "{}",
            Revision = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.AgentDefinitions.Add(definition);
        await dbContext.SaveChangesAsync();
        return definition.Id;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = Guid.NewGuid().ToString();

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                    services.AddDbContext<CSweetDbContext>(options =>
                        options.UseInMemoryDatabase(databaseName));
                });
            });
    }
}
