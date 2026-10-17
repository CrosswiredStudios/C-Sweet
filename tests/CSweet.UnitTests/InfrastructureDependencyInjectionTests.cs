using CSweet.AI.Providers;
using CSweet.Application.Core;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Application.SourceControl;
using CSweet.Contracts.SourceControl;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.SourceControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using CSweet.TrustedServices;
using System.Net;
using System.Net.Http.Json;

namespace CSweet.UnitTests;

public sealed class InfrastructureDependencyInjectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeliveryBranchCallsRequireTrustedHostConfigurationInTheCapabilityProcess(bool configured)
    {
        var builder = CreateInfrastructureBuilder();
        if (configured)
        {
            builder.Configuration["CSweet:SourceControl:GitHostBaseUrl"] = "http://localhost:54321";
            builder.Configuration["CSweet:SourceControl:TrustedServiceKeyBase64"] = Convert.ToBase64String(new byte[32]);
            builder.Configuration["CSweet:SourceControl:TrustedServiceKeyId"] = "core";
        }
        builder.AddCSweetInfrastructure();
        var transport = new DeliveryTransport();
        if (configured)
            builder.Services.AddHttpClient<TrustedSourceControlHostClient>().ConfigurePrimaryHttpMessageHandler(() => transport);
        using var host = builder.Build();
        var client = host.Services.GetRequiredService<ITrustedSourceControlHostClient>();
        var operation = new DeliveryBranchOperation(Guid.NewGuid(), Guid.NewGuid(), "InternalGit", null,
            "", "project", "ensure", "csweet/story/test", "main", "delivery-test");
        if (!configured)
        {
            Assert.IsType<UnavailableTrustedSourceControlHostClient>(client);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeliveryBranchAsync(operation));
            Assert.Equal("Delivery branch operations are unavailable.", error.Message);
            Assert.Equal(0, transport.Calls);
            return;
        }
        Assert.IsType<TrustedSourceControlHostClient>(client);
        var result = await client.DeliveryBranchAsync(operation);
        Assert.Equal("source-sha", result.SourceCommitSha);
        Assert.Equal(1, transport.Calls);
    }

    private sealed class DeliveryTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/internal/v3/delivery/branches", request.RequestUri!.AbsolutePath);
            Assert.Equal("core", Assert.Single(request.Headers.GetValues(TrustedServiceHeaders.KeyId)));
            Assert.True(request.Headers.Contains(TrustedServiceHeaders.Signature));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new DeliveryBranchResult("source-sha", "target-sha", null)) });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmployeeAndCollaborationServicesResolveWithoutAConstructionCycle(bool employeesFirst)
    {
        var builder = CreateInfrastructureBuilder();
        builder.AddCSweetInfrastructure();
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        await Task.Run(() =>
        {
            if (employeesFirst)
                scope.ServiceProvider.GetRequiredService<IOrganizationUserService>();
            else
                scope.ServiceProvider.GetRequiredService<IAgentCoordinationService>();

            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOrganizationUserService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAgentCoordinationService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPersonalTodoService>());
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HostWithInvalidWorkspaceSyncLimitsStartsUntilWorkspaceSyncIsUsed()
    {
        var builder = CreateInfrastructureBuilder();
        builder.Configuration["CSweet:SourceControl:WorkspaceSync:MaximumArchiveBytes"] = "0";
        builder.Configuration["CSweet:SourceControl:WorkspaceSync:MaximumExpandedBytes"] = "0";
        builder.Configuration["CSweet:SourceControl:WorkspaceSync:MaximumFileCount"] = "0";
        builder.AddCSweetInfrastructure();

        using var host = builder.Build();
        await host.StartAsync();

        Assert.Throws<OptionsValidationException>(() =>
            host.Services.GetRequiredService<IOptions<WorkspaceSyncTransferOptions>>().Value);

        await host.StopAsync();
    }

    [Fact]
    public void ResolvingLlmSecretStore_DoesNotRequireWritableParentDirectory()
    {
        var blocker = Path.GetTempFileName();
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                EnvironmentName = Environments.Production
            });
            builder.Configuration["ConnectionStrings:Postgres"] =
                "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused";
            builder.Configuration["CSweet:Secrets:FilePath"] =
                Path.Combine(blocker, "provider-secrets.json");
            builder.AddCSweetInfrastructure();

            using var host = builder.Build();
            var store = host.Services.GetRequiredService<ILlmProviderSecretStore>();

            Assert.IsType<FileLlmProviderSecretStore>(store);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void AgentHostBroker_PrefersConcreteAspireMcpEndpoint()
    {
        var builder = CreateInfrastructureBuilder();
        builder.Configuration["CSweet:AgentRuntime:AgentHostBroker:BaseUrl"] =
            "https+http://_mcp.agenthost";
        builder.Configuration["AGENTHOST_MCP"] = "http://localhost:54321";
        builder.AddCSweetInfrastructure();

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<AgentHostBrokerOptions>();

        Assert.Equal("http://localhost:54321", options.BaseUrl);
        Assert.Equal(new Uri("http://localhost:54321/"), options.ValidatedBaseUri());
    }

    [Fact]
    public void AgentHostBroker_DefaultTargetsNamedMcpEndpoint()
    {
        var options = new AgentHostBrokerOptions();

        Assert.Equal(new Uri("https+http://_mcp.agenthost/"), options.ValidatedBaseUri());
    }

    private static HostApplicationBuilder CreateInfrastructureBuilder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration["ConnectionStrings:Postgres"] =
            "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused";
        builder.Configuration["CSweet:Secrets:FilePath"] =
            Path.Combine(Path.GetTempPath(), $"csweet-tests-{Guid.NewGuid():N}", "provider-secrets.json");
        return builder;
    }
}
