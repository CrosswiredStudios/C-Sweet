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
using CSweet.Office.Contracts.ControlPlane;

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
        Assert.Contains(status.Steps, x => x.Key == "agent-execution" && x.IsRequired && !x.IsComplete);
        Assert.DoesNotContain(status.Steps, x => x.Key == "model-capability-test");
        Assert.DoesNotContain(status.Steps, x => x.Key == "admin-user");
    }

    [Fact]
    public async Task ExecutionCapacityStatus_IsActionableAndFailClosed()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/setup/execution-capacity");
        var status = await response.Content.ReadFromJsonAsync<ExecutionCapacityOnboardingResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(status);
        Assert.False(status.IsReady);
        Assert.NotEmpty(status.Checks);
    }

    [Fact]
    public async Task AssistedLocalSessionCreation_RequiresHostAdministration()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup/execution-capacity/local-sessions",
            new CreateLocalOfficeSetupSessionRequest("balanced", 2, 4096, 32768));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssistedLocalRedemption_IsAnonymousButRejectsUnknownHandoff()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/offices/local-sessions/redeem",
            new CSweet.Office.Contracts.ControlPlane.RedeemAssistedOfficeSetupRequest(
                "unknown-handoff", Environment.MachineName, "windows", "x64", "0.2.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssistedLocalRecoveryEndpoints_RejectUnknownOrUnauthenticatedAuthority()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            .ToString().ToLowerInvariant();

        var preflight = await client.PostAsJsonAsync("/api/offices/local-sessions/preflight",
            new AssistedOfficePreflightRequest("unknown-handoff", Environment.MachineName,
                "windows", architecture, "0.3.0", "clean"));
        var result = await client.PostAsJsonAsync("/api/offices/local-sessions/result",
            new ReportAssistedOfficeSetupResultRequest(Guid.NewGuid(), "unknown-receipt",
                "reconnect_unsafe", Environment.MachineName, "windows", architecture));
        var completion = await client.PostAsJsonAsync("/api/offices/local-sessions/removal-complete",
            new CompleteAssistedOfficeRemovalRequest("unknown-handoff", Environment.MachineName,
                "windows", architecture));
        var selection = await client.PostAsJsonAsync(
            $"/api/setup/execution-capacity/local-sessions/{Guid.NewGuid():D}/recovery",
            new SelectLocalOfficeRecoveryRequest("remove"));

        Assert.Equal(HttpStatusCode.BadRequest, preflight.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, completion.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, selection.StatusCode);
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
                    services.AddDbContext<CSweetDbContext>(options =>
                        options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                });
            });
    }
}
