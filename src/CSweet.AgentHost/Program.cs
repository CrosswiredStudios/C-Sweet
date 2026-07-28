using CSweet.AgentHost.Broker;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using CSweet.Application.Setup;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Agents;
using CSweet.Memory;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "first-party-agents.json"),
    optional: false,
    reloadOnChange: false);

builder.AddServiceDefaults();
builder.AddCSweetInfrastructure();
builder.Services.AddHostedService<AgentCatalogWarmupService>();
builder.Services.AddScoped<IPlatformCapabilityDispatcher, PlatformCapabilityDispatcher>();
builder.Services.AddScoped<McpToolCatalog>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AgentWorkInbox>();
builder.Services.AddScoped<AgentWorkRouter>();
builder.Services.AddScoped<McpAgentSessionService>();
builder.Services
    .AddOptions<AgentOnboardingDeliveryOptions>()
    .Bind(builder.Configuration.GetSection(AgentOnboardingDeliveryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddHostedService<ManagementReviewScheduler>();
builder.Services.AddHostedService<AgentOnboardingEventDispatcher>();
builder.Services.AddHostedService<AgentPlatformEventDispatcher>();
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
builder.Services.AddScoped<IPlatformCapabilityHandler, ManagementReportCapabilityHandler>();
builder.Services.AddScoped<IAgentMemoryIdentityResolver, AgentMemoryIdentityResolver>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("mcp-session", httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(
            httpContext.Request.Headers["Mcp-Session-Id"].FirstOrDefault()
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

app.UseRateLimiter();
app.MapCSweetMcpGateway();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new
{
    service = "CSweet.AgentHost",
    status = "ok",
    protocol = "csweet-plugin-v2",
    agentRuntime = "mcp-only"
}));

app.Run();

public partial class Program;
