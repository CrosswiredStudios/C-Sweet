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
    public async Task Install_CreatesGrantAndScheduleAndSupportsManagementActions()
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

        var installResponse = await client.PostAsJsonAsync(
            $"/api/agents/imports/{preview.ImportId}/install",
            new InstallAgentRequest(
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
                50));
        var installation = await installResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();

        Assert.Equal(HttpStatusCode.OK, installResponse.StatusCode);
        Assert.NotNull(installation);
        Assert.Equal("Periodic", installation.Schedule.ActivationMode);
        Assert.NotNull(installation.Schedule.NextTickAt);

        var listed = await client.GetFromJsonAsync<IReadOnlyList<AgentInstallationResponse>>(
            "/api/agents/installations");
        Assert.Single(listed!);
        Assert.Equal("Queued", listed![0].Build?.Status);
        Assert.Equal(6, listed[0].Build?.Steps?.Count);
        Assert.Equal("InProgress", listed[0].Build?.Steps?[0].Status);

        var scheduleResponse = await client.PutAsJsonAsync(
            $"/api/agents/installations/{installation.Id}/schedule",
            new UpdateAgentScheduleRequest("Manual", 1200, "Queue", 300, true));
        var scheduled = await scheduleResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();
        Assert.Equal(HttpStatusCode.OK, scheduleResponse.StatusCode);
        Assert.Equal("Manual", scheduled!.Schedule.ActivationMode);
        Assert.Null(scheduled.Schedule.NextTickAt);

        var runResponse = await client.PostAsync(
            $"/api/agents/installations/{installation.Id}/run-now",
            null);
        var run = await runResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        Assert.NotNull(run?.Schedule.RunRequestedAt);

        var disableResponse = await client.PostAsync(
            $"/api/agents/installations/{installation.Id}/disable",
            null);
        var disabled = await disableResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        Assert.False(disabled!.IsEnabled);
        Assert.False(disabled.Schedule.IsEnabled);

        var enableResponse = await client.PostAsync(
            $"/api/agents/installations/{installation.Id}/enable",
            null);
        var enabled = await enableResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        Assert.True(enabled!.IsEnabled);
        Assert.True(enabled.Schedule.IsEnabled);

        var buildLogResponse = await client.GetAsync(
            $"/api/agents/installations/{installation.Id}/build-log");
        var buildLog = await buildLogResponse.Content.ReadFromJsonAsync<AgentBuildLogResponse>();
        Assert.Equal(HttpStatusCode.OK, buildLogResponse.StatusCode);
        Assert.Equal("Queued", buildLog!.Status);
        Assert.Contains("Build job:", buildLog.Content, StringComparison.Ordinal);
        Assert.Contains("Build steps:", buildLog.Content, StringComparison.Ordinal);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var failedBuild = await dbContext.AgentBuildJobs.SingleAsync();
        failedBuild.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        (await dbContext.AgentPackageVersions.SingleAsync()).Status = AgentPackageVersionStatus.Failed;
        var suppressedSchedule = await dbContext.AgentSchedules.SingleAsync();
        suppressedSchedule.ConsecutiveStartupFailures = 3;
        suppressedSchedule.AutomaticStartSuppressedAt = DateTimeOffset.UtcNow;
        suppressedSchedule.NextTickAt = null;
        await dbContext.SaveChangesAsync();

        var retryResponse = await client.PostAsync(
            $"/api/agents/installations/{installation.Id}/retry-build",
            null);
        var retried = await retryResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        Assert.Equal("Queued", retried!.Build?.Status);
        Assert.Equal(2, retried.Build?.Attempt);
        Assert.Equal(0, retried.Schedule.ConsecutiveStartupFailures);
        Assert.Null(retried.Schedule.AutomaticStartSuppressedAt);

        (await dbContext.AgentPackageVersions.SingleAsync()).Status = AgentPackageVersionStatus.Built;
        suppressedSchedule.ActivationMode = ActivationMode.AlwaysOn;
        suppressedSchedule.ConsecutiveStartupFailures = 3;
        suppressedSchedule.AutomaticStartSuppressedAt = DateTimeOffset.UtcNow;
        suppressedSchedule.NextTickAt = null;
        await dbContext.SaveChangesAsync();

        var startupRetryResponse = await client.PostAsync(
            $"/api/agents/installations/{installation.Id}/retry-startup",
            null);
        var startupRetried = await startupRetryResponse.Content.ReadFromJsonAsync<AgentInstallationResponse>();
        Assert.Equal(HttpStatusCode.OK, startupRetryResponse.StatusCode);
        Assert.Equal(0, startupRetried!.Schedule.ConsecutiveStartupFailures);
        Assert.Null(startupRetried.Schedule.AutomaticStartSuppressedAt);
        Assert.NotNull(startupRetried.Schedule.NextTickAt);

        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = DateTimeOffset.UtcNow
        };
        runtime.Events.Add(new AgentRuntimeEvent
        {
            Id = Guid.NewGuid(),
            AgentRuntimeInstanceId = runtime.Id,
            Status = AgentRuntimeStatus.Queued,
            Reason = "Run requested.",
            OccurredAt = runtime.QueuedAt
        });
        dbContext.AgentRuntimeInstances.Add(runtime);
        await dbContext.SaveChangesAsync();

        var runs = await client.GetFromJsonAsync<IReadOnlyList<AgentRuntimeRunResponse>>(
            $"/api/agents/installations/{installation.Id}/runs");
        Assert.Single(runs!);
        Assert.Equal("Queued", runs![0].Status);
        Assert.Single(runs[0].Events);
        var detail = await client.GetFromJsonAsync<AgentInstallationResponse>(
            $"/api/agents/installations/{installation.Id}");
        Assert.Equal("Queued", detail!.LatestRuntime?.Status);

        Assert.Single(await dbContext.AgentInstallations.ToListAsync());
        Assert.Single(await dbContext.AgentInstallationGrants.ToListAsync());
        Assert.Single(await dbContext.AgentSchedules.ToListAsync());
        Assert.Equal(2, await dbContext.AgentBuildJobs.CountAsync());
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
