using CSweet.AI.Providers;
using CSweet.Application.Core;
using CSweet.Application.Communications;
using CSweet.Application.WorkManagement;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.SourceControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class InfrastructureDependencyInjectionTests
{
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
