using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginBootstrapWorker(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<PluginBootstrapWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var child in configuration.GetSection("CSweet:Plugins:Bootstrap").GetChildren())
        {
            var options = child.Get<PluginBootstrapOptions>();
            if (options?.Enabled != true) continue;
            try { await ReconcileAsync(child.Key, options, stoppingToken); }
            catch (Exception exception) { logger.LogError(exception, "Plugin bootstrap {PluginName} failed.", child.Key); }
        }
    }

    private async Task ReconcileAsync(string name, PluginBootstrapOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        using var scope = scopeFactory.CreateScope();
        var imports = scope.ServiceProvider.GetRequiredService<IPluginImportService>();
        var preview = await imports.PreviewAsync(new PreviewAgentImportRequest(options.RepositoryUrl, options.CommitSha), cancellationToken);
        if (!string.Equals(preview.CommitSha, options.CommitSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(preview.ManifestDigest, options.ExpectedManifestDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Bootstrap commit or manifest digest did not match the immutable configuration.");

        var installations = scope.ServiceProvider.GetRequiredService<IPluginInstallationService>();
        var existing = (await installations.ListAsync(cancellationToken)).FirstOrDefault(x =>
            x.CommitSha.Equals(preview.CommitSha, StringComparison.OrdinalIgnoreCase) &&
            x.AgentId == preview.AgentId && x.BusinessId == options.BusinessId);
        var installation = existing ?? await installations.InstallAsync(preview.ImportId, new InstallAgentRequest(
            options.BusinessId, options.ActivationMode, options.TickFrequencySeconds, options.OverlapPolicy,
            options.GrantedCapabilities, options.GrantedSubscriptions, options.GrantedPublications,
            options.GrantedPermissions, options.GrantedNetworkAccess, options.MaxRuntimeSeconds,
            options.MemoryMb, options.CpuPercent)
        {
            PluginScope = options.Scope,
            GrantedRequestedCapabilities = options.GrantedRequestedCapabilities,
            ConfigurationSettings = options.Configuration.ToDictionary(
                pair => pair.Key,
                pair => System.Text.Json.JsonSerializer.SerializeToElement(pair.Value),
                StringComparer.Ordinal)
        }, cancellationToken);

        var secrets = scope.ServiceProvider.GetRequiredService<IPluginSecretStore>();
        foreach (var pair in options.SecretEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(pair.Value);
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException($"Bootstrap secret environment variable '{pair.Value}' is not set.");
            await secrets.SetAsync(installation.Id, pair.Key, value, cancellationToken);
        }
        logger.LogInformation("Plugin bootstrap {PluginName} reconciled installation {InstallationId} at {CommitSha}.",
            name, installation.Id, preview.CommitSha);
    }

    private static void Validate(PluginBootstrapOptions options)
    {
        if (!Uri.TryCreate(options.RepositoryUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Bootstrap RepositoryUrl must be an absolute HTTPS URL.");
        if (options.CommitSha.Length != 40 || options.CommitSha.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Bootstrap CommitSha must be an exact 40-character commit SHA.");
        if (options.ExpectedManifestDigest.Length != 64 || options.ExpectedManifestDigest.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Bootstrap ExpectedManifestDigest must be a SHA-256 digest.");
    }
}

public sealed class PluginBootstrapOptions
{
    public bool Enabled { get; set; }
    public string RepositoryUrl { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string ExpectedManifestDigest { get; set; } = string.Empty;
    public string BusinessId { get; set; } = "system";
    public string Scope { get; set; } = "System";
    public string ActivationMode { get; set; } = "AlwaysOn";
    public int TickFrequencySeconds { get; set; } = 300;
    public string OverlapPolicy { get; set; } = "Skip";
    public int MaxRuntimeSeconds { get; set; } = 86400;
    public int MemoryMb { get; set; } = 512;
    public int CpuPercent { get; set; } = 100;
    public List<string> GrantedCapabilities { get; set; } = [];
    public List<string> GrantedRequestedCapabilities { get; set; } = [];
    public List<string> GrantedSubscriptions { get; set; } = [];
    public List<string> GrantedPublications { get; set; } = [];
    public List<string> GrantedPermissions { get; set; } = [];
    public List<string> GrantedNetworkAccess { get; set; } = [];
    public Dictionary<string, string> Configuration { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SecretEnvironmentVariables { get; set; } = new(StringComparer.Ordinal);
}
