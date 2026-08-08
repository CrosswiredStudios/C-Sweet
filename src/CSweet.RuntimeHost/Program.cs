using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.AppleVirtualization;
using CSweet.AgentRuntime.Firecracker;
using CSweet.AgentRuntime.HyperV;
using CSweet.AgentRuntime.LocalRpc;
using CSweet.AgentRuntime.Protocol;
using CSweet.RuntimeHost;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "CSweet.RuntimeHost");

var endpoint = builder.Configuration
    .GetSection(RuntimeHostEndpointOptions.SectionName)
    .Get<RuntimeHostEndpointOptions>() ?? new RuntimeHostEndpointOptions();
endpoint.Validate();
var authentication = builder.Configuration
    .GetSection(RuntimeHostAuthenticationOptions.SectionName)
    .Get<RuntimeHostAuthenticationOptions>() ?? new RuntimeHostAuthenticationOptions();
var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
authentication.LoadSharedKeyFileIfNeeded(Path.Combine(
    string.IsNullOrWhiteSpace(commonData) ? AppContext.BaseDirectory : commonData,
    "CSweet", "AgentRuntime", "runtime-host.key"));

builder.Services.AddSingleton(endpoint);
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuntimeHostRequestAuthenticator>();
builder.Services.AddSingleton<RuntimeHostRequestDispatcher>();
builder.Services.AddSingleton<RuntimeHostRpcServer>();
builder.Services.AddHostedService<RuntimeHostWorker>();
builder.Services.AddHostedService<RuntimeHostWorkloadReaper>();

var hyperV = builder.Configuration.GetSection("CSweet:AgentRuntime:Providers:HyperV")
    .Get<HyperVIsolationBackendOptions>() ?? new HyperVIsolationBackendOptions();
var firecracker = builder.Configuration.GetSection("CSweet:AgentRuntime:Providers:Firecracker")
    .Get<FirecrackerIsolationBackendOptions>() ?? new FirecrackerIsolationBackendOptions();
var apple = builder.Configuration.GetSection("CSweet:AgentRuntime:Providers:AppleVirtualization")
    .Get<AppleVirtualizationIsolationBackendOptions>() ?? new AppleVirtualizationIsolationBackendOptions();
builder.Services.AddSingleton(hyperV);
builder.Services.AddSingleton(firecracker);
builder.Services.AddSingleton(apple);
builder.Services.AddSingleton<IPlatformIsolationBackend, HyperVIsolationBackend>();
builder.Services.AddSingleton<IPlatformIsolationBackend, FirecrackerIsolationBackend>();
builder.Services.AddSingleton<IPlatformIsolationBackend, AppleVirtualizationIsolationBackend>();

await builder.Build().RunAsync();
