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
var trustedServiceKey = EnsureTrustedServiceKey(
    builder.Configuration["CSweet:SourceControl:TrustedServiceKeyBase64"]);
var agentBrokerKey = DeriveScopedKey(trustedServiceKey, "csweet-agent-broker-v2");
var sourceAccessAppId = builder.Configuration["CSweet:SourceControl:SourceAccessAppId"];
var sourceAccessPrivateKey = builder.Configuration["CSweet:SourceControl:SourceAccessPrivateKeyBase64"];
var provisionerAppId = builder.Configuration["CSweet:SourceControl:ProvisionerAppId"];
var provisionerPrivateKey = builder.Configuration["CSweet:SourceControl:ProvisionerPrivateKeyBase64"];
var sourceAccessInstallUrl = builder.Configuration["CSweet:SourceControl:SourceAccessInstallUrl"];
var provisionerInstallUrl = builder.Configuration["CSweet:SourceControl:ProvisionerInstallUrl"];

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

var workerHost = builder.AddProject<Projects.CSweet_WorkerHost>("workerhost")
    .WithReference(api)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WaitForCompletion(migrator)
    .WaitFor(api);

var gitHost = builder.AddProject<Projects.CSweet_GitHost>("githost")
    .WithEnvironment("TrustedServiceAuthentication__KeyId", "core")
    .WithEnvironment("TrustedServiceAuthentication__SharedKeyBase64", trustedServiceKey);
if (HasGitHubAppConfiguration(sourceAccessAppId, sourceAccessPrivateKey))
{
    gitHost.WithEnvironment("GitHubApp__AppId", sourceAccessAppId!)
        .WithEnvironment("GitHubApp__PrivateKeyBase64", sourceAccessPrivateKey!);
}
var gitHostEndpoint = gitHost.GetEndpoint("http");

api.WithReference(gitHost)
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyId", "core")
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyBase64", trustedServiceKey)
    .WithEnvironment("CSweet__SourceControl__GitHostBaseUrl", gitHostEndpoint)
    .WaitFor(gitHost);
api.WithEnvironment("CSweet__SourceControl__AgentBrokerKeyId", "agenthost")
    .WithEnvironment("CSweet__SourceControl__AgentBrokerKeyBase64", agentBrokerKey!);
agentHost.WithEnvironment("CSweet__SourceControl__AgentBrokerKeyId", "agenthost")
    .WithEnvironment("CSweet__SourceControl__AgentBrokerKeyBase64", agentBrokerKey!)
    .WithEnvironment("CSweet__SourceControl__CoreBrokerBaseUrl", api.GetEndpoint("http"));
if (!string.IsNullOrWhiteSpace(sourceAccessInstallUrl))
    api.WithEnvironment("CSweet__SourceControl__SourceAccessInstallUrl", sourceAccessInstallUrl);
workerHost.WithReference(gitHost)
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyId", "core")
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyBase64", trustedServiceKey)
    .WithEnvironment("CSweet__SourceControl__GitHostBaseUrl", gitHostEndpoint)
    .WaitFor(gitHost);

var provisionerHost = builder.AddProject<Projects.CSweet_SourceControlProvisionerHost>("provisionerhost")
    .WithEnvironment("TrustedServiceAuthentication__KeyId", "core")
    .WithEnvironment("TrustedServiceAuthentication__SharedKeyBase64", trustedServiceKey);
if (HasGitHubAppConfiguration(provisionerAppId, provisionerPrivateKey))
{
    provisionerHost.WithEnvironment("GitHubApp__AppId", provisionerAppId!)
        .WithEnvironment("GitHubApp__PrivateKeyBase64", provisionerPrivateKey!);
}
var provisionerHostEndpoint = provisionerHost.GetEndpoint("http");

api.WithReference(provisionerHost)
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyId", "core")
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyBase64", trustedServiceKey)
    .WithEnvironment("CSweet__SourceControl__ProvisionerHostBaseUrl", provisionerHostEndpoint)
    .WaitFor(provisionerHost);
if (!string.IsNullOrWhiteSpace(provisionerInstallUrl))
    api.WithEnvironment("CSweet__SourceControl__ProvisionerInstallUrl", provisionerInstallUrl);
workerHost.WithReference(provisionerHost)
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyId", "core")
    .WithEnvironment("CSweet__SourceControl__TrustedServiceKeyBase64", trustedServiceKey)
    .WithEnvironment("CSweet__SourceControl__ProvisionerHostBaseUrl", provisionerHostEndpoint)
    .WaitFor(provisionerHost);

builder.AddProject<Projects.CSweet_App>("app", launchProfileName: appLaunchProfile)
    .WithHttpEndpoint(port: 5097, name: "http")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();

static bool HasGitHubAppConfiguration(params string?[] values) =>
    values.All(value => !string.IsNullOrWhiteSpace(value));

static string EnsureTrustedServiceKey(string? configured)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(configured) &&
            Convert.FromBase64String(configured).Length >= 32)
            return configured;
    }
    catch (FormatException)
    {
        // Generate an ephemeral per-AppHost key below.
    }
    return Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}

static string? DeriveScopedKey(string? rootKeyBase64, string purpose)
{
    if (string.IsNullOrWhiteSpace(rootKeyBase64))
        return null;
    try
    {
        var rootKey = Convert.FromBase64String(rootKeyBase64);
        if (rootKey.Length < 32)
            return null;
        return Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(
            rootKey,
            System.Text.Encoding.UTF8.GetBytes(purpose)));
    }
    catch (FormatException)
    {
        return null;
    }
}

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
