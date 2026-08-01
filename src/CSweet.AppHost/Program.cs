var builder = DistributedApplication.CreateBuilder(args);

// Visual Studio can start the AppHost debug session without providing the
// DEBUG_SESSION_INFO payload Aspire needs to launch child projects through the
// IDE. In that case, force Aspire's normal process launcher so the projects
// receive their required `dotnet run --project ...` arguments.
if (OperatingSystem.IsWindows() &&
    string.Equals(Environment.GetEnvironmentVariable("VSIDE"), "true", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEBUG_SESSION_INFO")))
{
    builder.Configuration["DEBUG_SESSION_PORT"] = null;
}

var postgresUserName = builder.AddParameterFromConfiguration(
    "postgres-username",
    "CSweet:Postgres:UserName");

var postgresPassword = builder.AddParameterFromConfiguration(
    "postgres-password",
    "CSweet:Postgres:Password",
    secret: true);

var postgresDatabaseName = builder.Configuration["CSweet:Postgres:Database"]
    ?? throw new InvalidOperationException("CSweet:Postgres:Database must be configured for AppHost.");

var postgresServer = builder.AddPostgres("postgres", userName: postgresUserName, password: postgresPassword)
    .WithDataVolume("csweet-aspire-postgres");
var postgres = postgresServer.AddDatabase("csweet", postgresDatabaseName);

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var localAgentDirectory = ResolveLocalAgentDirectory(
    builder.Configuration["CSweet:AgentCatalog:LocalDirectoryPath"],
    repositoryRoot);
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var localStateDirectory = string.IsNullOrWhiteSpace(localAppData)
    ? Path.Combine(repositoryRoot, ".csweet")
    : Path.Combine(localAppData, "CSweet");
Directory.CreateDirectory(localStateDirectory);
var appLaunchProfile = builder.Configuration["CSweet:App:LaunchProfile"]
    ?? "http-no-wasm-debug";
var marketplaceEnabled = builder.Configuration["CSweet:Marketplace:Enabled"] ?? "false";
var marketplaceBaseUrl = builder.Configuration["CSweet:Marketplace:BaseUrl"]
    ?? "https://marketplace.csweet.com/";
var marketplaceTimeoutSeconds =
    builder.Configuration["CSweet:Marketplace:TimeoutSeconds"] ?? "10";

var migrator = builder.AddProject<Projects.CSweet_Migrator>("migrator")
    .WithReference(postgres)
    .WaitFor(postgres);

// Agent runtimes execute on private Docker networks. Keeping the broker in a
// real container lets the runtime manager attach only this gateway to each
// network instead of exposing the runtime to the host or Aspire network.
var agentHost = builder.AddDockerfile(
        "agenthost",
        repositoryRoot,
        Path.Combine("docker", "agenthost.Dockerfile"))
    .WithContainerName("agenthost")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithHttpEndpoint(targetPort: 8081, name: "mcp")
    .WithEnvironment("Mcp__PublicEndpoint", "http://agenthost:8081/mcp")
    .WithBindMount(localStateDirectory, "/state")
    .WithEnvironment("CSweet__Secrets__FilePath", "/state/provider-secrets.json")
    .WithEnvironment("CSweet__GenAi__MediaRoot", "/state/media")
    .WithEnvironment("CSweet__Marketplace__Enabled", marketplaceEnabled)
    .WithEnvironment("CSweet__Marketplace__BaseUrl", marketplaceBaseUrl)
    .WithEnvironment("CSweet__Marketplace__TimeoutSeconds", marketplaceTimeoutSeconds)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WaitForCompletion(migrator);
var agentHostEndpoint = agentHost.GetEndpoint("mcp");

var api = builder.AddProject<Projects.CSweet_Api>("api")
    .WithReference(postgres)
    .WithReference(agentHostEndpoint)
    .WithEnvironment("CSweet__AgentCatalog__LocalDirectoryPath", localAgentDirectory)
    .WithEnvironment("CSweet__GenAi__MediaRoot", Path.Combine(localStateDirectory, "media"))
    .WithEnvironment("CSweet__Marketplace__Enabled", marketplaceEnabled)
    .WithEnvironment("CSweet__Marketplace__BaseUrl", marketplaceBaseUrl)
    .WithEnvironment("CSweet__Marketplace__TimeoutSeconds", marketplaceTimeoutSeconds)
    .WaitFor(postgres)
    .WaitFor(agentHost)
    .WaitForCompletion(migrator);

builder.AddProject<Projects.CSweet_App>("app", launchProfileName: appLaunchProfile)
    .WithHttpEndpoint(port: 5097, name: "http")
    .WithReference(api)
    .WaitFor(api);

builder.AddProject<Projects.CSweet_WorkerHost>("workerhost")
    .WithReference(api)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WaitForCompletion(migrator)
    .WaitFor(api);

builder.Build().Run();

static string ResolveLocalAgentDirectory(string? configured, string repositoryRoot)
{
    if (!string.IsNullOrWhiteSpace(configured))
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(repositoryRoot, configured));

    var workspaceRoot = Directory.GetParent(repositoryRoot)?.FullName;
    if (!string.IsNullOrWhiteSpace(workspaceRoot) && ContainsAgentCheckout(workspaceRoot))
        return workspaceRoot;

    return Path.Combine(repositoryRoot, "Plugins", "Agents");
}

static bool ContainsAgentCheckout(string directory)
{
    try
    {
        return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
            .Any(candidate => File.Exists(Path.Combine(candidate, "csweet-plugin.json")));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}
