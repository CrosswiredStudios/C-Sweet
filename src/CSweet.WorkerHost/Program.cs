using Microsoft.Extensions.Hosting;
using CSweet.WorkerHost;
using CSweet.Infrastructure;
using CSweet.Infrastructure.Setup;
using CSweet.Infrastructure.Communications;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (health checks, OpenTelemetry, resilience)
builder.AddServiceDefaults();
builder.AddCSweetInfrastructure();
builder.Services.AddCommunicationPluginRuntime();

builder.Services.AddHostedService<AgentRuntimeStartupCleanupWorker>();
builder.Services.AddHostedService<AgentBuildWorker>();
builder.Services.AddHostedService<AgentScheduleWorker>();
builder.Services.AddHostedService<AgentRuntimeCleanupWorker>();
builder.Services.AddHostedService<GitWorkspaceRetentionCleanupWorker>();
builder.Services.AddHostedService<PluginBootstrapWorker>();
builder.Services.AddHostedService<CommunicationDeliveryWorker>();

var host = builder.Build();
host.Run();
