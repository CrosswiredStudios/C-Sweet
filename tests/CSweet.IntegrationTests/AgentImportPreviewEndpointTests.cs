using System.Net;
using System.Net.Http.Json;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public class AgentImportPreviewEndpointTests
{
    [Fact]
    public async Task Preview_IsRateLimitedAfterTenRequestsPerMinute()
    {
        using var factory = CreateFactory();
        await MarkSetupCompleteAsync(factory);
        var client = factory.CreateClient();
        HttpResponseMessage? response = null;
        for (var index = 0; index < 11; index++)
        {
            response?.Dispose();
            response = await client.PostAsJsonAsync(
                "/api/agents/imports/preview",
                new PreviewAgentImportRequest("https://github.com/example/research-agent"));
        }
        using (response)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode);
        }
    }

    [Fact]
    public async Task Post_PreviewsAndPersistsRootManifest()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await MarkSetupCompleteAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/agents/imports/preview",
            new PreviewAgentImportRequest("https://github.com/example/research-agent"));
        var preview = await response.Content.ReadFromJsonAsync<AgentImportPreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(preview);
        Assert.Equal("com.example.research-agent", preview.AgentId);
        Assert.Equal("Previewed", preview.Status);
        Assert.Equal("src/ResearchAgent/ResearchAgent.csproj", preview.ProjectPath);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Single(await dbContext.AgentPackageSources.ToListAsync());
        Assert.Single(await dbContext.AgentPackageVersions.ToListAsync());
        Assert.Single(await dbContext.AuditEvents
            .Where(x => x.EventType == "agent-import.previewed")
            .ToListAsync());
    }

    [Fact]
    public async Task Post_RejectsUnsupportedRepositoryUrl()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await MarkSetupCompleteAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/agents/imports/preview",
            new PreviewAgentImportRequest("https://gitlab.com/example/research-agent"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("GitHub", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Install_CreatesDefinitionAndBuilderJobWithoutBusinessRuntimeState()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await MarkSetupCompleteAsync(factory);
        var settingsResponse = await client.PutAsJsonAsync(
            "/api/agent-runtime/settings",
            new UpdateAgentRuntimeSettingsRequest(EnableImportedAgents: true));
        settingsResponse.EnsureSuccessStatusCode();
        var previewResponse = await client.PostAsJsonAsync(
            "/api/agents/imports/preview",
            new PreviewAgentImportRequest("https://github.com/example/research-agent"));
        var preview = await previewResponse.Content.ReadFromJsonAsync<AgentImportPreviewResponse>();
        Assert.NotNull(preview);

        var installRequest = new InstallAgentRequest(
                "default",
                "Periodic",
                900,
                "Skip",
                ["research.execute.v1"],
                [],
                [],
                [],
                [],
                600,
                512,
                50);
        var installResponse = await client.PostAsJsonAsync(
            $"/api/agents/imports/{preview.ImportId}/install",
            installRequest);
        var definition = await installResponse.Content.ReadFromJsonAsync<AgentDefinitionResponse>();

        Assert.Equal(HttpStatusCode.OK, installResponse.StatusCode);
        Assert.NotNull(definition);
        Assert.Equal("com.example.research-agent", definition.AgentId);
        Assert.Equal("Periodic", definition.DefaultActivationMode);
        Assert.False(definition.IsAvailableForHire);
        Assert.Equal("Queued", definition.Build?.Status);
        Assert.Equal(6, definition.Build?.Steps?.Count);
        Assert.Equal("InProgress", definition.Build?.Steps?[0].Status);

        var listedDefinitions = await client.GetFromJsonAsync<IReadOnlyList<AgentDefinitionResponse>>(
            "/api/agents/definitions");
        var listedInstallations = await client.GetFromJsonAsync<IReadOnlyList<AgentInstallationResponse>>(
            "/api/agents/installations");
        Assert.Single(listedDefinitions!);
        Assert.Empty(listedInstallations!);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Single(await dbContext.AgentDefinitions.ToListAsync());
        Assert.Single(await dbContext.AgentDefinitionConfigurations.ToListAsync());
        Assert.Single(await dbContext.AgentBuildJobs.ToListAsync());
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
        Assert.Empty(await dbContext.AgentInstallationGrants.ToListAsync());
        Assert.Empty(await dbContext.AgentSchedules.ToListAsync());
        Assert.Empty(await dbContext.AgentRuntimeInstances.ToListAsync());

        var pluginBypassResponse = await client.PostAsJsonAsync(
            $"/api/plugins/imports/{preview.ImportId}/install",
            installRequest);
        Assert.Equal(HttpStatusCode.BadRequest, pluginBypassResponse.StatusCode);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

    private static async Task MarkSetupCompleteAsync(WebApplicationFactory<Program> factory)
    {
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        dbContext.SystemConfigurations.Add(new CSweet.Domain.Setup.SystemConfiguration
        {
            Id = Guid.NewGuid(),
            IsFirstRunComplete = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();
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
                    services.RemoveAll<IGitHubAgentRepositoryClient>();
                    services.AddScoped<IGitHubAgentRepositoryClient, FakeGitHubAgentRepositoryClient>();
                });
            });
    }

    private sealed class FakeGitHubAgentRepositoryClient : IGitHubAgentRepositoryClient
    {
        private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("""
            {
              "manifestVersion": "2.0",
              "kind": "agent",
              "id": "com.example.research-agent",
              "name": "Research Agent",
              "version": "1.2.3",
              "catalog": {
                "role": { "key": "researcher", "name": "Researcher" },
                "license": { "spdxId": "MIT" },
                "iconUrls": ["https://example.com/research-agent.png"]
              },
              "publisher": { "id": "com.example", "name": "Example" },
              "runtime": {
                "type": "dotnet-project",
                "projectPath": "src/ResearchAgent/ResearchAgent.csproj",
                "targetFramework": "net10.0",
                "defaultActivationMode": "Periodic",
                "supportsMultipleInstallations": true,
                "maximumConcurrentJobs": 4
              },
              "protocol": { "minimumVersion": "2.0", "maximumVersion": "2.x" },
              "provides": [{
                "name": "research.execute.v1",
                "description": "Execute a research request.",
                "inputSchema": { "type": "object", "additionalProperties": true },
                "outputSchema": { "type": "object", "additionalProperties": true },
                "executionTimeoutSeconds": 120,
                "idempotency": "work-item"
              }],
              "requires": [],
              "events": { "subscribes": [] },
              "configuration": [],
              "credentials": [],
              "webAccess": { "mode": "None", "rules": [] },
              "ui": []
            }
            """);

        public Task<string> GetDefaultBranchAsync(
            string repositoryOwner,
            string repositoryName,
            CancellationToken cancellationToken) => Task.FromResult("main");

        public Task<string> ResolveCommitShaAsync(
            string repositoryOwner,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken) =>
            Task.FromResult("0123456789abcdef0123456789abcdef01234567");

        public Task<byte[]> GetRootManifestAsync(
            string repositoryOwner,
            string repositoryName,
            string commitSha,
            CancellationToken cancellationToken) => Task.FromResult(Manifest);
    }
}
