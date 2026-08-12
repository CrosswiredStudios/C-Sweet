using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.LocalRpc;
using CSweet.AgentRuntime.Protocol;
using CSweet.ExecutionNode;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "CSweet.ExecutionNode");
builder.Services.AddSystemd();

var options = builder.Configuration.GetSection(ExecutionNodeOptions.SectionName)
    .Get<ExecutionNodeOptions>() ?? new ExecutionNodeOptions();
if (!Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out var controlPlane) ||
    controlPlane.Scheme != Uri.UriSchemeHttps)
    throw new InvalidOperationException("CSweet:ExecutionNode:ControlPlaneUrl must be an absolute HTTPS URL.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ExecutionNodeStateStore>();
builder.Services.AddSingleton<RuntimeHostInventory>();
builder.Services.AddSingleton<ExecutionNodeArtifactCache>();
builder.Services.AddHostedService<ExecutionNodeWorker>();
builder.Services.AddHttpClient("control-plane", client => client.BaseAddress = controlPlane);
builder.Services.AddSingleton(TimeProvider.System);

var endpoint = builder.Configuration.GetSection(RuntimeHostEndpointOptions.SectionName)
    .Get<RuntimeHostEndpointOptions>() ?? new RuntimeHostEndpointOptions();
endpoint.Validate();
builder.Services.AddSingleton(endpoint);
var authentication = builder.Configuration.GetSection(RuntimeHostAuthenticationOptions.SectionName)
    .Get<RuntimeHostAuthenticationOptions>() ?? new RuntimeHostAuthenticationOptions();
var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
authentication.LoadSharedKeyFileIfNeeded(Path.Combine(
    string.IsNullOrWhiteSpace(common) ? AppContext.BaseDirectory : common,
    "CSweet", "AgentRuntime", "runtime-host.key"));
builder.Services.AddSingleton(authentication);
builder.Services.AddSingleton<RuntimeHostRequestAuthenticator>();
builder.Services.AddSingleton<IAgentIsolationProvider>(services => CreateClient(
    IsolationProviderCatalog.HyperV(), services));
builder.Services.AddSingleton<IAgentIsolationProvider>(services => CreateClient(
    IsolationProviderCatalog.Firecracker(), services));
builder.Services.AddSingleton<IAgentIsolationProvider>(services => CreateClient(
    IsolationProviderCatalog.AppleVirtualization(), services));

await builder.Build().RunAsync();

static RuntimeHostProviderClient CreateClient(IsolationProviderDescriptor descriptor, IServiceProvider services) =>
    new(descriptor,
        services.GetRequiredService<RuntimeHostEndpointOptions>(),
        services.GetRequiredService<RuntimeHostRequestAuthenticator>(),
        services.GetRequiredService<ILogger<RuntimeHostProviderClient>>());
