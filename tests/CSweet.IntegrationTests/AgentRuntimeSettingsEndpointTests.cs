using System.Net;
using System.Net.Http.Json;
using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public class AgentRuntimeSettingsEndpointTests
{
    [Fact]
    public async Task Get_ReturnsSeededDefaults()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await PrepareDatabaseAsync(factory, client);

        var settings = await client.GetFromJsonAsync<AgentRuntimeSettingsResponse>(
            "/api/agent-runtime/settings");

        Assert.NotNull(settings);
        Assert.False(settings.EnableImportedAgents);
        Assert.Equal("OnDemand", settings.DefaultActivationMode);
        Assert.Equal(3600, settings.DefaultTickFrequencySeconds);
        Assert.Equal(300, settings.MinimumTickFrequencySeconds);
    }

    [Fact]
    public async Task Put_PersistsPartialUpdateAndWritesAuditEvent()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await PrepareDatabaseAsync(factory, client);

        var response = await client.PutAsJsonAsync(
            "/api/agent-runtime/settings",
            new UpdateAgentRuntimeSettingsRequest(
                EnableImportedAgents: true,
                DefaultTickFrequencySeconds: 1800));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await client.GetFromJsonAsync<AgentRuntimeSettingsResponse>(
            "/api/agent-runtime/settings");
        Assert.NotNull(updated);
        Assert.True(updated.EnableImportedAgents);
        Assert.Equal(1800, updated.DefaultTickFrequencySeconds);
        Assert.Equal(300, updated.MinimumTickFrequencySeconds);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        Assert.Single(await dbContext.AuditEvents
            .Where(x => x.EventType == "agent-runtime.settings.updated")
            .ToListAsync());
    }

    [Fact]
    public async Task Put_RejectsInvalidPartialUpdate()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await PrepareDatabaseAsync(factory, client);

        var response = await client.PutAsJsonAsync(
            "/api/agent-runtime/settings",
            new UpdateAgentRuntimeSettingsRequest(MinimumTickFrequencySeconds: 4000));
        var result = await response.Content.ReadFromJsonAsync<AgentRuntimeSettingsActionResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Contains("Default tick frequency", result.Message);
    }

    [Fact]
    public async Task Recover_RunsImmediateRuntimeReconciliation()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await PrepareDatabaseAsync(factory, client);

        var response = await client.PostAsync("/api/agent-runtime/settings/recover", null);
        var result = await response.Content.ReadFromJsonAsync<AgentRuntimeSettingsActionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        Assert.Contains("reconciliation completed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PrepareDatabaseAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client)
    {
        var response = await client.GetAsync("/api/setup/status");
        response.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CSweetDbContext>();
        var configuration = await dbContext.SystemConfigurations.SingleAsync();
        configuration.IsFirstRunComplete = true;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
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
                });
            });
    }
}
