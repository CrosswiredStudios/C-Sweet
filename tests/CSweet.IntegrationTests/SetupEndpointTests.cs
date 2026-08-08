using System.Net;
using System.Net.Http.Json;
using CSweet.AgentRuntime.Abstractions;
using CSweet.Contracts.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CSweet.IntegrationTests;

public class SetupEndpointTests
{
    [Fact]
    public async Task Get_SetupStatus_ReturnsOk()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/setup/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FreshDatabase_ReturnsFirstRunIncomplete()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");

        Assert.NotNull(status);
        Assert.False(status.IsFirstRunComplete);
        Assert.Equal(6, status.Steps.Count);
        Assert.DoesNotContain(status.Steps, x => x.Key == "finish");
        Assert.Contains(status.Steps, x => x.Key == "email-delivery" && !x.IsRequired);
        Assert.Contains(status.Steps, x => x.Key == "genai-provider" && !x.IsRequired);
        Assert.Contains(status.Steps, x => x.Key == "agent-isolation" && !x.IsRequired);
        Assert.DoesNotContain(status.Steps, x => x.Key == "model-capability-test");
        Assert.DoesNotContain(status.Steps, x => x.Key == "admin-user");
    }

    [Fact]
    public async Task AgentIsolationStatus_IsActionableAndFailClosed()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/setup/agent-isolation");
        var status = await response.Content.ReadFromJsonAsync<AgentIsolationOnboardingResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(status);
        Assert.Equal("hyperv-gen2", status.ProviderId);
        Assert.False(status.IsReady);
        Assert.NotEmpty(status.Checks);
        Assert.Contains(status.Checks, check => check.Key == "runtime-host");
        Assert.Contains(status.Checks, check => check.Key == "provider-certification");
    }

    [Fact]
    public async Task CommunicationsOptions_ReturnsGuidedFirstPartyCatalog()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var options = await client.GetFromJsonAsync<CommunicationSetupOptionsResponse>(
            "/api/setup/communications/options");

        Assert.NotNull(options);
        Assert.Equal(4, options.FirstPartyPlugins.Count);
        Assert.Collection(
            options.FirstPartyPlugins,
            plugin => Assert.Equal("discord", plugin.Key),
            plugin => Assert.Equal("slack", plugin.Key),
            plugin => Assert.Equal("teams", plugin.Key),
            plugin => Assert.Equal("whatsapp", plugin.Key));
        Assert.All(options.FirstPartyPlugins, plugin =>
        {
            Assert.StartsWith("com.csweet.communication.", plugin.PluginId, StringComparison.Ordinal);
            Assert.StartsWith("https://", plugin.DocumentationUrl, StringComparison.Ordinal);
            Assert.StartsWith("https://", plugin.ServicePortalUrl, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Post_SetupComplete_FailsWhenPrerequisitesAreMissing()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/setup/complete", content: null);
        var result = await response.Content.ReadFromJsonAsync<SetupActionResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal("provider_profile_required", result.ErrorCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<DbContextOptions<CSweetDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<CSweetDbContext>>();
                    // Endpoint tests must not inherit a developer workstation's installed
                    // RuntimeHost. With no certified provider registered, onboarding must
                    // remain actionable and fail closed on every platform.
                    services.RemoveAll<IAgentIsolationProvider>();
                    services.AddDbContext<CSweetDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                });
            });
    }
}
