using Microsoft.Extensions.Configuration;
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
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var localStateDirectory = string.IsNullOrWhiteSpace(localAppData)
    ? Path.Combine(repositoryRoot, ".csweet")
    : Path.Combine(localAppData, "CSweet");
Directory.CreateDirectory(localStateDirectory);
var executionGatewayCertificate = EnsureDevelopmentExecutionGatewayCertificate(localStateDirectory);
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
var sourceAccessClientId = builder.Configuration["CSweet:SourceControl:SourceAccessClientId"];
var sourceAccessClientSecret = builder.Configuration["CSweet:SourceControl:SourceAccessClientSecret"];
var provisionerClientId = builder.Configuration["CSweet:SourceControl:ProvisionerClientId"];
var provisionerClientSecret = builder.Configuration["CSweet:SourceControl:ProvisionerClientSecret"];

var migrator = builder.AddProject<Projects.CSweet_Migrator>("migrator")
    .WithReference(postgres)
    .WaitFor(postgres);

// AgentHost is an unprivileged broker/control-plane process. It has no hypervisor,
// host filesystem, or Docker authority; privileged VM lifecycle is isolated in RuntimeHost.
var agentHost = builder.AddProject<Projects.CSweet_AgentHost>("agenthost")
    .WithHttpEndpoint(name: "http")
    .WithHttpEndpoint(name: "mcp")
    .WithEnvironment("CSweet__Secrets__FilePath", Path.Combine(localStateDirectory, "provider-secrets.json"))
    .WithEnvironment("CSweet__GenAi__MediaRoot", Path.Combine(localStateDirectory, "media"))
    .WithEnvironment("CSweet__Marketplace__Enabled", marketplaceEnabled)
    .WithEnvironment("CSweet__Marketplace__BaseUrl", marketplaceBaseUrl)
    .WithEnvironment("CSweet__Marketplace__TimeoutSeconds", marketplaceTimeoutSeconds)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WaitForCompletion(migrator);
var agentHostEndpoint = agentHost.GetEndpoint("mcp");
agentHost.WithEnvironment("Mcp__PublicEndpoint", agentHostEndpoint);

var api = builder.AddProject<Projects.CSweet_Api>("api")
    .WithReference(postgres)
    .WithReference(agentHostEndpoint)
    .WithEnvironment("CSweet__AgentRuntime__AgentHostBroker__BaseUrl", agentHostEndpoint)
    .WithEnvironment("CSweet__GenAi__MediaRoot", Path.Combine(localStateDirectory, "media"))
    .WithEnvironment("CSweet__Marketplace__Enabled", marketplaceEnabled)
    .WithEnvironment("CSweet__Marketplace__BaseUrl", marketplaceBaseUrl)
    .WithEnvironment("CSweet__Marketplace__TimeoutSeconds", marketplaceTimeoutSeconds)
    .WaitFor(postgres)
    .WaitFor(agentHost)
    .WaitForCompletion(migrator);
if (OperatingSystem.IsWindows())
{
    var workspaceRoot = Directory.GetParent(repositoryRoot)?.FullName;
    var launcher = Path.Combine(repositoryRoot, "src", "CSweet.Api", "Setup",
        "Start-CSweetDevelopmentOfficeSetup.ps1");
    var officeBootstrap = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.Combine(workspaceRoot,
        "CSweet.Office", "scripts", "windows", "Initialize-CSweetWindowsIsolationTest.ps1");
    if (File.Exists(launcher) && officeBootstrap is not null && File.Exists(officeBootstrap))
    {
        api.WithEnvironment("CSweet__ExecutionFleet__WindowsDevelopmentLauncherScript", launcher)
            .WithEnvironment("CSweet__ExecutionFleet__WindowsDevelopmentOfficeBootstrapScript", officeBootstrap);
    }
}

var executionGateway = builder.AddProject<Projects.CSweet_ExecutionGateway>("executiongateway")
    .WithHttpsEndpoint(name: "https")
    .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", executionGatewayCertificate.Path)
    .WithReference(postgres)
    .WithReference(agentHostEndpoint)
    .WithEnvironment("CSweet__AgentRuntime__AgentHostBroker__BaseUrl", agentHostEndpoint)
    .WaitFor(postgres)
    .WaitFor(agentHost)
    .WaitForCompletion(migrator);
executionGateway
    .WithEnvironment("CSweet__ExecutionFleet__PublicLaunchEnabled", "true")
    .WithEnvironment("CSweet__ExecutionFleet__AllowUnpinnedDevelopmentImages", "true");
var executionGatewayEndpoint = executionGateway.GetEndpoint("https");
var executionGatewayBootstrapEndpoint = executionGateway.GetEndpoint("http");
api.WithReference(executionGateway)
    .WithEnvironment("CSweet__ExecutionGateway__PublicUrl", executionGatewayEndpoint)
    .WithEnvironment("CSweet__ExecutionGateway__BootstrapUrl", executionGatewayBootstrapEndpoint)
    .WithEnvironment("CSweet__ExecutionGateway__PublicCertificateSha256", executionGatewayCertificate.Sha256)
    .WithEnvironment("CSweet__ExecutionFleet__PublicLaunchEnabled", "true")
    .WithEnvironment("CSweet__ExecutionFleet__AllowUnpinnedDevelopmentImages", "true")
    .WaitFor(executionGateway);

var workerHost = builder.AddProject<Projects.CSweet_WorkerHost>("workerhost")
    .WithReference(api)
    .WithReference(postgres)
    .WithReference(agentHostEndpoint)
    .WithEnvironment("CSweet__AgentRuntime__AgentHostBroker__BaseUrl", agentHostEndpoint)
    .WithEnvironment("CSweet__ExecutionFleet__PublicLaunchEnabled", "true")
    .WithEnvironment("CSweet__ExecutionFleet__AllowUnpinnedDevelopmentImages", "true")
    .WaitFor(postgres)
    .WaitForCompletion(migrator)
    .WaitFor(agentHost)
    .WaitFor(api);

var gitHost = builder.AddProject<Projects.CSweet_GitHost>("githost")
    .WithEnvironment("TrustedServiceAuthentication__KeyId", "core")
    .WithEnvironment("TrustedServiceAuthentication__SharedKeyBase64", trustedServiceKey);
if (HasGitHubAppConfiguration(sourceAccessAppId, sourceAccessPrivateKey))
{
    gitHost.WithEnvironment("GitHubApp__AppId", sourceAccessAppId!)
        .WithEnvironment("GitHubApp__PrivateKeyBase64", sourceAccessPrivateKey!);
}
foreach (var setting in builder.Configuration.GetSection("CSweet:SourceControl:Storage").AsEnumerable())
{
    if (setting.Value is not null)
        gitHost.WithEnvironment(setting.Key.Replace(":", "__"), setting.Value);
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
if (!string.IsNullOrWhiteSpace(sourceAccessClientId))
    api.WithEnvironment("CSweet__SourceControl__SourceAccessClientId", sourceAccessClientId);
if (!string.IsNullOrWhiteSpace(sourceAccessClientSecret))
    api.WithEnvironment("CSweet__SourceControl__SourceAccessClientSecret", sourceAccessClientSecret);
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
if (!string.IsNullOrWhiteSpace(provisionerClientId))
    api.WithEnvironment("CSweet__SourceControl__ProvisionerClientId", provisionerClientId);
if (!string.IsNullOrWhiteSpace(provisionerClientSecret))
    api.WithEnvironment("CSweet__SourceControl__ProvisionerClientSecret", provisionerClientSecret);
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

static (string Path, string Sha256) EnsureDevelopmentExecutionGatewayCertificate(string stateDirectory)
{
    var certificatePath = Path.Combine(stateDirectory, "development-execution-gateway.pfx");
    if (File.Exists(certificatePath))
    {
        try
        {
            using var existing = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12FromFile(
                    certificatePath,
                    password: null,
                    System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            if (existing.HasPrivateKey &&
                existing.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
                return (certificatePath, existing.GetCertHashString(
                    System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant());
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Replace a corrupt or incomplete AppHost-owned certificate below.
        }
    }

    using var key = System.Security.Cryptography.RSA.Create(2048);
    var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
        "CN=localhost",
        key,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature |
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment,
            true));
    request.CertificateExtensions.Add(
        new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            new System.Security.Cryptography.OidCollection
            {
                new("1.3.6.1.5.5.7.3.1")
            },
            true));
    var subjectAlternativeName =
        new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
    subjectAlternativeName.AddDnsName("localhost");
    subjectAlternativeName.AddIpAddress(System.Net.IPAddress.Loopback);
    subjectAlternativeName.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
    request.CertificateExtensions.Add(subjectAlternativeName.Build());

    using var generated = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddYears(2));
    File.WriteAllBytes(certificatePath, generated.Export(
        System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
    return (certificatePath, generated.GetCertHashString(
        System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant());
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
