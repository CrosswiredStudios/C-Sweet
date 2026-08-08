using CSweet.AI.Providers;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CSweet.UnitTests;

public sealed class InfrastructureDependencyInjectionTests
{
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
