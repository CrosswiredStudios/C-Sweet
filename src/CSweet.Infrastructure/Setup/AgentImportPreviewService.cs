using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlatformCapabilities = CSweet.Agent.SDK.PlatformCapabilities;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed partial class AgentImportPreviewService : IPluginImportService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CSweetDbContext _dbContext;
    private readonly IGitHubAgentRepositoryClient _repositoryClient;
    private readonly IAuditEventWriter _auditWriter;
    private readonly IPluginManifestReader _manifestReader;

    public AgentImportPreviewService(
        CSweetDbContext dbContext,
        IGitHubAgentRepositoryClient repositoryClient,
        IAuditEventWriter auditWriter,
        IPluginManifestReader? manifestReader = null)
    {
        _dbContext = dbContext;
        _repositoryClient = repositoryClient;
        _auditWriter = auditWriter;
        _manifestReader = manifestReader ?? new PluginManifestReader();
    }

    public async Task<AgentImportPreviewResponse> PreviewAsync(
        PreviewAgentImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var repository = GitHubRepositoryUrlNormalizer.Normalize(request.RepositoryUrl);
        if (request.Ref?.Length > 255)
        {
            throw new AgentImportPreviewException("Git reference cannot exceed 255 characters.");
        }

        var defaultBranch = await _repositoryClient.GetDefaultBranchAsync(
            repository.Owner,
            repository.Name,
            cancellationToken);
        var reference = string.IsNullOrWhiteSpace(request.Ref) ? defaultBranch : request.Ref.Trim();
        var commitSha = await _repositoryClient.ResolveCommitShaAsync(
            repository.Owner,
            repository.Name,
            reference,
            cancellationToken);
        var manifestSource = await _repositoryClient.GetRootPluginManifestAsync(
            repository.Owner,
            repository.Name,
            commitSha,
            cancellationToken);
        var manifestBytes = manifestSource.Content;

        PluginManifestEnvelope pluginEnvelope;
        try { pluginEnvelope = _manifestReader.Read(manifestBytes, manifestSource.FileName); }
        catch (JsonException exception)
        {
            throw new AgentImportPreviewException($"Plugin manifest is not valid: {exception.Message}", exception);
        }

        PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(manifestBytes, SerializerOptions)
                ?? throw new AgentImportPreviewException("Plugin manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new AgentImportPreviewException(
                $"Plugin manifest is not valid JSON: {exception.Message}",
                exception);
        }

        ValidateManifest(manifest);
        var warnings = CreateWarnings(manifest);
        var digest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;

        var source = await _dbContext.AgentPackageSources
            .SingleOrDefaultAsync(x => x.RepositoryUrl == repository.RepositoryUrl, cancellationToken);
        if (source is null)
        {
            source = new AgentPackageSource
            {
                Id = Guid.NewGuid(),
                RepositoryUrl = repository.RepositoryUrl,
                Host = "github.com",
                RepositoryOwner = repository.Owner,
                RepositoryName = repository.Name,
                DefaultBranch = defaultBranch,
                CreatedAt = now,
                UpdatedAt = now
            };
            _dbContext.AgentPackageSources.Add(source);
        }
        else
        {
            source.DefaultBranch = defaultBranch;
            source.UpdatedAt = now;
        }

        var version = await _dbContext.AgentPackageVersions
            .SingleOrDefaultAsync(
                x => x.PackageSourceId == source.Id &&
                     x.CommitSha == commitSha &&
                     x.ManifestDigest == digest,
                cancellationToken);
        var isNewVersion = version is null;
        if (version is null)
        {
            version = new AgentPackageVersion
            {
                Id = Guid.NewGuid(),
                PackageSourceId = source.Id,
                CommitSha = commitSha,
                ManifestDigest = digest,
                CapabilityDescriptorsDigest = CapabilityDescriptorsDigest(manifest),
                ManifestJson = Encoding.UTF8.GetString(manifestBytes),
                PluginKind = ParsePluginKind(pluginEnvelope.Kind),
                ManifestFileName = pluginEnvelope.ManifestFileName,
                AgentId = manifest.Id,
                AgentName = manifest.Name,
                Version = manifest.Version,
                PublisherId = manifest.Publisher.Id,
                PublisherName = manifest.Publisher.Name,
                RuntimeType = manifest.Runtime.Type,
                ProjectPath = manifest.Runtime.ProjectPath,
                TargetFramework = manifest.Runtime.TargetFramework,
                DefaultActivationMode = manifest.Runtime.DefaultActivationMode,
                WarningsJson = JsonSerializer.Serialize(warnings, SerializerOptions),
                Status = AgentPackageVersionStatus.Previewed,
                ImportedAt = now
            };
            _dbContext.AgentPackageVersions.Add(version);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (isNewVersion)
        {
            await _auditWriter.WriteAsync(
                "agent-import.previewed",
                nameof(AgentPackageVersion),
                version.Id,
                $"Previewed {manifest.Id} {manifest.Version} from {repository.RepositoryUrl}.",
                null,
                cancellationToken);
        }

        return ToResponse(version, source, manifest, warnings);
    }

    private static PluginKind ParsePluginKind(string value) => value.ToLowerInvariant() switch
    {
        "agent" => PluginKind.Agent,
        "service" => PluginKind.Service,
        _ => throw new AgentImportPreviewException($"Unsupported plugin kind '{value}'.")
    };

    public static void ValidateManifest(PluginManifest manifest)
    {
        var errors = new List<string>();
        AddRequiredIdentifierError(manifest.Id, "id", errors);
        AddRequiredError(manifest.Name, "name", errors);

        if (string.IsNullOrWhiteSpace(manifest.Version) || !SemanticVersionRegex().IsMatch(manifest.Version))
        {
            errors.Add("Plugin manifest version must be a semantic version such as 1.2.3.");
        }

        if (manifest.Publisher is null)
        {
            errors.Add("Agent manifest publisher is required.");
        }
        else
        {
            AddRequiredIdentifierError(manifest.Publisher.Id, "publisher.id", errors);
            AddRequiredError(manifest.Publisher.Name, "publisher.name", errors);
        }

        if (manifest.Runtime is null)
        {
            errors.Add("Agent manifest runtime is required.");
        }
        else
        {
            if (!string.Equals(manifest.Runtime.Type, "dotnet-project", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Imported GitHub plugins must use runtime.type 'dotnet-project'.");
            }

            ValidateProjectPath(manifest.Runtime.ProjectPath, errors);

            if (string.IsNullOrWhiteSpace(manifest.Runtime.TargetFramework) ||
                !TargetFrameworkRegex().IsMatch(manifest.Runtime.TargetFramework))
            {
                errors.Add("runtime.targetFramework must be a .NET target framework such as net10.0.");
            }

            if (manifest.Runtime.DefaultActivationMode is not null &&
                manifest.Runtime.DefaultActivationMode is not ("AlwaysOn" or "Periodic" or "Manual"))
            {
                errors.Add("runtime.defaultActivationMode must be AlwaysOn, Periodic, or Manual.");
            }

            if (manifest.Runtime.MaximumConcurrentJobs < 1)
            {
                errors.Add("runtime.maximumConcurrentJobs must be at least one.");
            }

            if (manifest.Runtime.EnvironmentProfile is not null &&
                !System.Text.RegularExpressions.Regex.IsMatch(
                    manifest.Runtime.EnvironmentProfile,
                    "^[a-z0-9][a-z0-9.-]{0,127}$"))
            {
                errors.Add("runtime.environmentProfile is invalid.");
            }

            if (manifest.Runtime.WorkspaceAccess is not ("None" or "ReadOnly" or "ReadWrite"))
            {
                errors.Add("runtime.workspaceAccess must be None, ReadOnly, or ReadWrite.");
            }
        }

        if (!string.Equals(manifest.ManifestVersion, "2.0", StringComparison.Ordinal))
            errors.Add("Executable plugins must use manifestVersion 2.0.");
        if (manifest.Protocol is null ||
            string.IsNullOrWhiteSpace(manifest.Protocol.MinimumVersion) ||
            string.IsNullOrWhiteSpace(manifest.Protocol.MaximumVersion))
        {
            errors.Add("Agent manifest protocol minimumVersion and maximumVersion are required.");
        }
        else if (!string.Equals(manifest.Protocol.MinimumVersion, "2.0", StringComparison.Ordinal) ||
                 !manifest.Protocol.MaximumVersion.StartsWith("2.", StringComparison.Ordinal))
        {
            errors.Add("Executable plugins must require MCP runtime protocol 2.0 through 2.x.");
        }

        AddListError(manifest.Provides.Select(x => x.Name).ToArray(), "provides", errors);
        foreach (var capability in manifest.Provides)
        {
            if (string.IsNullOrWhiteSpace(capability.Description))
                errors.Add($"provides capability '{capability.Name}' requires a description.");
            if (capability.InputSchema.ValueKind != JsonValueKind.Object ||
                capability.OutputSchema.ValueKind != JsonValueKind.Object)
                errors.Add($"provides capability '{capability.Name}' requires object inputSchema and outputSchema values.");
            if (capability.ExecutionTimeoutSeconds is < 1 or > 900)
                errors.Add($"provides capability '{capability.Name}' executionTimeoutSeconds must be between 1 and 900.");
            if (capability.Idempotency is not ("work-item" or "caller-key" or "none"))
                errors.Add($"provides capability '{capability.Name}' idempotency is unsupported.");
            var descriptorHash = DescriptorHash(capability);
            if (!string.IsNullOrWhiteSpace(capability.DescriptorHash) &&
                !string.Equals(capability.DescriptorHash, descriptorHash, StringComparison.OrdinalIgnoreCase))
                errors.Add($"provides capability '{capability.Name}' descriptorHash does not match its canonical descriptor.");
        }
        AddListError(manifest.Requires.Select(x => x.Name).ToArray(), "requires", errors);
        AddListError(manifest.Events.Subscribes, "events.subscribes", errors);
        if (manifest.Events.Publishes.Count > 0)
            errors.Add("events.publishes is not supported in protocol v2; use explicit capabilities or work progress.");
        ValidateConfigurationContract(manifest, errors);
        ValidateWebAccess(manifest, errors);

        if (errors.Count > 0)
        {
            throw new AgentImportPreviewException(string.Join(" ", errors));
        }
    }

    private static void ValidateProjectPath(string? projectPath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            errors.Add("runtime.projectPath is required for dotnet-project agents.");
            return;
        }

        var segments = projectPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(projectPath) ||
            projectPath.StartsWith('/') ||
            projectPath.StartsWith('\\') ||
            segments.Contains("..", StringComparer.Ordinal) ||
            !projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("runtime.projectPath must be a relative .csproj path without parent traversal.");
        }
    }

    private static IReadOnlyList<AgentManifestWarningResponse> CreateWarnings(PluginManifest manifest)
    {
        var warnings = new List<AgentManifestWarningResponse>();
        if (manifest.WebAccess.Mode != PluginWebAccessMode.None)
        {
            warnings.Add(new AgentManifestWarningResponse(
                "network_access_requested",
                manifest.WebAccess.Mode == PluginWebAccessMode.AllPublic
                    ? "This plugin requests broker-proxied access to the entire public web and requires a separate high-risk acknowledgement."
                    : "This plugin requests broker-proxied access to specific external destinations."));
        }

        if (manifest.Requires.Count > 0)
        {
            warnings.Add(new AgentManifestWarningResponse(
                "capabilities_requested",
                "Required capabilities are declarations only and must be approved during installation."));
        }

        if (manifest.Runtime.DefaultActivationMode == "AlwaysOn")
        {
            warnings.Add(new AgentManifestWarningResponse(
                "always_on_requested",
                "Always-on activation for community plugins is subject to global policy."));
        }

        return warnings;
    }

    private static AgentImportPreviewResponse ToResponse(
        AgentPackageVersion version,
        AgentPackageSource source,
        PluginManifest manifest,
        IReadOnlyList<AgentManifestWarningResponse> warnings) =>
        new AgentImportPreviewResponse(
            version.Id,
            source.RepositoryUrl,
            version.CommitSha,
            version.ManifestDigest,
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Publisher.Id,
            manifest.Publisher.Name,
            manifest.Runtime.Type,
            manifest.Runtime.ProjectPath,
            manifest.Runtime.TargetFramework,
            manifest.Runtime.DefaultActivationMode,
            manifest.Provides.Select(x => x.Name).ToArray(),
            manifest.Events.Subscribes,
            manifest.Events.Publishes,
            [],
            WebGrantTokens(manifest),
            warnings,
            version.Status.ToString())
        {
            PluginKind = version.PluginKind.ToString(),
            ManifestFileName = version.ManifestFileName,
            RequestedCapabilities = GrantRequiredCapabilities(manifest),
            WebAccess = manifest.WebAccess,
            ConfigurationFields = manifest.Configuration,
            CredentialBindings = manifest.Credentials
        };

    public static IReadOnlyList<string> WebGrantTokens(PluginManifest manifest) => manifest.WebAccess.Mode switch
    {
        PluginWebAccessMode.None => [],
        PluginWebAccessMode.AllPublic => ["all-public"],
        _ => manifest.WebAccess.Rules.Select(WebGrantToken).ToArray()
    };

    public static IReadOnlyList<string> GrantRequiredCapabilities(PluginManifest manifest) =>
        manifest.Requires.Select(x => x.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string CapabilityDescriptorsDigest(PluginManifest manifest)
    {
        var canonical = string.Join(
            "\n",
            manifest.Provides
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(DescriptorHash));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string DescriptorHash(PluginCapabilityDeclaration capability)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            capability.Name,
            capability.Description,
            inputSchema = capability.InputSchema,
            outputSchema = capability.OutputSchema,
            capability.ExecutionTimeoutSeconds,
            capability.Idempotency,
            capability.RiskClass
        }, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    public static string WebGrantToken(PluginWebAccessRule rule)
    {
        var port = rule.Port is null ? string.Empty : $":{rule.Port}";
        var methods = string.Join(',', rule.Methods.Select(x => x.ToUpperInvariant()).Order(StringComparer.Ordinal));
        return $"{rule.Protocol.ToLowerInvariant()}|{rule.Scheme.ToLowerInvariant()}://{rule.Host.ToLowerInvariant()}{port}{rule.PathPrefix}|{methods}|{rule.Credential ?? string.Empty}";
    }

    private static void ValidateConfigurationContract(PluginManifest manifest, ICollection<string> errors)
    {
        var provided = manifest.Provides
            .Select(capability => capability.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (string.Equals(manifest.Kind, "agent", StringComparison.OrdinalIgnoreCase) &&
            manifest.Configuration.Count > 0)
        {
            if (!provided.Contains(AgentConfigurationCapabilities.Describe))
                errors.Add($"Configurable agents must provide '{AgentConfigurationCapabilities.Describe}'.");
            if (!provided.Contains(AgentConfigurationCapabilities.Update))
                errors.Add($"Configurable agents must provide '{AgentConfigurationCapabilities.Update}'.");
        }

        foreach (var contribution in manifest.Ui.Where(x => !string.IsNullOrWhiteSpace(x.Capability)))
        {
            if (!provided.Contains(contribution.Capability!))
                errors.Add($"UI contribution '{contribution.Id}' references capability '{contribution.Capability}' that is not declared in provides.");
        }
    }

    private static void ValidateWebAccess(PluginManifest manifest, List<string> errors)
    {
        if (manifest.WebAccess.Mode == PluginWebAccessMode.None && manifest.WebAccess.Rules.Count > 0)
            errors.Add("webAccess.rules must be empty when mode is None.");
        if (manifest.WebAccess.Mode == PluginWebAccessMode.Allowlist && manifest.WebAccess.Rules.Count == 0)
            errors.Add("webAccess.rules is required when mode is Allowlist.");
        if (manifest.WebAccess.Mode == PluginWebAccessMode.AllPublic && manifest.WebAccess.Rules.Count > 0)
            errors.Add("webAccess.rules must be empty when mode is AllPublic.");

        var credentials = manifest.Credentials
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        if (credentials.Count != manifest.Credentials.Count)
            errors.Add("credentials must have unique, non-empty names.");
        foreach (var rule in manifest.WebAccess.Rules)
        {
            if (rule.Protocol is not ("http" or "websocket")) errors.Add("webAccess rule protocol must be http or websocket.");
            if (rule.Scheme is not ("http" or "https" or "wss")) errors.Add("webAccess rule scheme must be http, https, or wss.");
            if (rule.Protocol == "http" && rule.Scheme is not ("http" or "https"))
                errors.Add("HTTP webAccess rules must use http or https.");
            if (rule.Protocol == "websocket" && (rule.Scheme != "wss" || rule.Methods.Count != 1 || rule.Methods[0] != "GET"))
                errors.Add("WebSocket webAccess rules must use wss and GET.");
            if (string.IsNullOrWhiteSpace(rule.Host) || Uri.CheckHostName(rule.Host) == UriHostNameType.Unknown)
                errors.Add("webAccess rule host must be a DNS hostname.");
            if (!rule.PathPrefix.StartsWith('/')) errors.Add("webAccess rule pathPrefix must start with '/'.");
            if (rule.PathPrefix.Contains("..", StringComparison.Ordinal)) errors.Add("webAccess rule pathPrefix cannot contain parent traversal.");
            if (string.IsNullOrWhiteSpace(rule.Purpose)) errors.Add("webAccess rule purpose is required.");
            if (rule.Methods.Count == 0 || rule.Methods.Any(x => x is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE")))
                errors.Add("webAccess rule methods contains an unsupported HTTP method.");
            if (rule.Credential is not null)
            {
                if (!credentials.TryGetValue(rule.Credential, out var credential))
                    errors.Add($"webAccess rule references unknown credential '{rule.Credential}'.");
                else
                {
                    var port = rule.Port is null ? string.Empty : $":{rule.Port}";
                    var origin = $"{rule.Scheme}://{rule.Host}{port}";
                    if (!credential.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"Credential '{rule.Credential}' is not bound to webAccess origin '{origin}'.");
                }
            }
        }
    }

    private static void AddRequiredIdentifierError(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdentifierRegex().IsMatch(value))
        {
            errors.Add($"Plugin manifest {fieldName} must contain letters, numbers, dots, underscores, or hyphens.");
        }
    }

    private static void AddRequiredError(string? value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Plugin manifest {fieldName} is required.");
        }
    }

    private static void AddListError(IReadOnlyList<string>? values, string fieldName, List<string> errors)
    {
        if (values is null || values.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"Plugin manifest {fieldName} must be an array of non-empty strings.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,198}[A-Za-z0-9])?$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex("^net\\d+\\.\\d+(?:-[A-Za-z0-9.-]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkRegex();
}
