using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure;
using CSweet.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGrpc();
builder.AddCSweetInfrastructure();
builder.Services
    .AddOptions<AgentBrokerPolicyOptions>()
    .Bind(builder.Configuration.GetSection(AgentBrokerPolicyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<ConfiguredAgentAuthorizationPolicy>();
builder.Services.AddScoped<IAgentAuthorizationPolicy, PersistedAgentAuthorizationPolicy>();
builder.Services.AddSingleton<AgentSessionRegistry>();
builder.Services.AddScoped<IPlatformCapabilityDispatcher, PlatformCapabilityDispatcher>();
builder.Services.AddSingleton<McpToolCatalog>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddOptions<AgentOnboardingDeliveryOptions>()
    .Bind(builder.Configuration.GetSection(AgentOnboardingDeliveryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<ManagementReviewScheduler>();
builder.Services.AddHostedService<AgentOnboardingEventDispatcher>();
builder.Services.AddScoped<IAgentRuntimeSignalService, AgentRuntimeSignalService>();
builder.Services.AddScoped<AgentEmployeeIdentityResolver>();
builder.Services.AddScoped<PlatformLlmCapabilityHandler>();
builder.Services.AddScoped<IPlatformCapabilityHandler, PlatformGenAiCapabilityHandler>();
builder.Services.AddHostedService<CSweet.Infrastructure.GenAi.GenAiJobWorker>();
builder.Services.AddSingleton<IMemoryStore>(_ => new PostgreSqlMemoryStore(
    builder.Configuration.GetConnectionString("Postgres")
    ?? builder.Configuration.GetConnectionString("csweet")
    ?? throw new InvalidOperationException("A PostgreSQL connection is required for platform memory.")));
builder.Services.AddScoped<PlatformMemoryCapabilityHandler>();
builder.Services.AddScoped<PlatformWebProxyCapabilityHandler>();
builder.Services.AddScoped<PlatformWebSocketCapabilityHandler>();
builder.Services.AddScoped<IPlatformCapabilityHandler, LlmPlatformCapabilityAdapter>();
builder.Services.AddScoped<IPlatformCapabilityHandler, MemoryPlatformCapabilityAdapter>();
builder.Services.AddScoped<IPlatformCapabilityHandler, WebPlatformCapabilityAdapter>();
builder.Services.AddScoped<IPlatformCapabilityHandler, WebSocketPlatformCapabilityAdapter>();
builder.Services.AddScoped<IPlatformCapabilityHandler, WorkforcePlatformCapabilityHandler>();
builder.Services.AddScoped<CSweet.Agent.SDK.IWorkforceCatalogProvider>(services =>
    services.GetRequiredService<CSweet.Infrastructure.Marketplace.MarketplaceDiscoveryClient>());
if (builder.Environment.IsDevelopment() &&
    builder.Configuration.GetValue<bool>("DevelopmentMarketplace:Enabled"))
    builder.Services.AddScoped<CSweet.Agent.SDK.IWorkforceCatalogProvider, DevelopmentWorkforceMarketplaceProvider>();
builder.Services.AddScoped<IPlatformCapabilityHandler, CommunicationHubCapabilityHandler>();
builder.Services.AddScoped<IPlatformCapabilityHandler, AgentOnboardingCapabilityHandler>();
builder.Services.AddScoped<IPlatformEventObserver, ManagementEventObserver>();
builder.Services.AddScoped<IAgentMemoryIdentityResolver, AgentMemoryIdentityResolver>();

var app = builder.Build();

app.MapGrpcService<AgentBrokerService>();
app.MapCSweetMcpGateway();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    service = "CSweet.AgentHost",
    status = "ok",
    protocol = "csweet-plugin-v1"
}));

app.Run();

public partial class Program;
