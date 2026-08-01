using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Agents;
using CSweet.Application.Marketplace;
using CSweet.Application.Setup;
using CSweet.Contracts.Marketplace;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Marketplace;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Agents;

public sealed class AgentCatalogOptions
{
    public const string SectionName = "CSweet:AgentCatalog";

    public string LocalDirectoryPath { get; set; } = Path.Combine("Plugins", "Agents");
    public int MaximumSourceSizeMb { get; set; } = 100;
    public int MaximumFileCount { get; set; } = 10_000;
}

public sealed class AgentCatalogService(
    IEnumerable<IAgentCatalogProvider> providers,
    ILogger<AgentCatalogService> logger) : IAgentCatalogService
{
    public async Task<AvailableAgentSearchResult> GetAvailableAgentsAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var normalized = query with
        {
            RequiredCapabilities = query.RequiredCapabilities ?? [],
            Limit = Math.Clamp(query.Limit, 1, 100)
        };
        var agents = new List<AvailableAgent>();
        var health = new List<AgentCatalogSourceHealth>();
        foreach (var provider in providers.OrderBy(x => SourcePriority(x.Source)))
        {
            try
            {
                var result = await provider.SearchAsync(organizationId, normalized, cancellationToken);
                agents.AddRange(result.Agents);
                health.Add(result.Health);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Agent catalog source {Source} failed.", provider.Source);
                health.Add(new AgentCatalogSourceHealth(provider.Source, false, "The source is unavailable."));
            }
        }

        var filtered = agents
            .Where(agent => Matches(agent, normalized))
            .Select(agent => agent with { Score = Score(agent, normalized) })
            .GroupBy(DeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(Consolidate)
            .ToList();

        filtered = (normalized.Sort?.Trim().ToLowerInvariant()) switch
        {
            "rating" => filtered.OrderByDescending(x => x.Rating).ThenByDescending(x => x.Score).ToList(),
            "price-low" => filtered.OrderBy(x => x.Price ?? decimal.MaxValue).ThenByDescending(x => x.Score).ToList(),
            "name" => filtered.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => filtered.OrderByDescending(x => x.Score).ThenBy(x => SourcePriority(x.Source)).ToList()
        };

        return new AvailableAgentSearchResult(
            filtered.Take(normalized.Limit).ToArray(),
            health.GroupBy(x => x.Source).Select(x => x.First()).OrderBy(x => SourcePriority(x.Source)).ToArray());
    }

    public async Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentReference)) return null;
        var source = ParseSource(agentReference);
        var provider = providers.FirstOrDefault(x => x.Source == source);
        return provider is null
            ? null
            : await provider.ResolveAsync(organizationId, agentReference, cancellationToken);
    }

    private static bool Matches(AvailableAgent agent, AvailableAgentSearchQuery query)
    {
        if (query.RequiredCapabilities is { Count: > 0 } &&
            query.RequiredCapabilities.Except(agent.Capabilities, StringComparer.OrdinalIgnoreCase).Any())
            return false;
        if (!string.IsNullOrWhiteSpace(query.Category) &&
            !string.Equals(query.Category.Trim(), agent.Category, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.MaximumPrice is { } maximum && agent.Price is { } price && price > maximum)
            return false;
        if (!string.IsNullOrWhiteSpace(query.Currency) && agent.Price is not null &&
            !string.Equals(query.Currency.Trim(), agent.Currency, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(query.Role) && !Contains(agent, query.Role.Trim()))
            return false;
        if (!string.IsNullOrWhiteSpace(query.SearchString))
        {
            var tokens = query.SearchString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0 && !tokens.All(token => Contains(agent, token))) return false;
        }
        return true;
    }

    private static bool Contains(AvailableAgent agent, string value) =>
        agent.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        agent.Summary.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        agent.Publisher.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        agent.Category.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        agent.RoleAliases.Any(x => x.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
        agent.Keywords.Any(x => x.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
        agent.Capabilities.Any(x => x.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static decimal Score(AvailableAgent agent, AvailableAgentSearchQuery query)
    {
        decimal score = 0.5m;
        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            var role = query.Role.Trim();
            score += string.Equals(agent.Name, role, StringComparison.OrdinalIgnoreCase) ||
                     agent.RoleAliases.Contains(role, StringComparer.OrdinalIgnoreCase)
                ? 0.30m
                : 0.20m;
        }
        if (!string.IsNullOrWhiteSpace(query.SearchString)) score += 0.10m;
        if (query.RequiredCapabilities is { Count: > 0 }) score += 0.05m;
        score += agent.Source switch
        {
            AgentCatalogSource.Installed => 0.05m,
            AgentCatalogSource.LocalDirectory => 0.04m,
            AgentCatalogSource.FirstPartyCatalog => 0.03m,
            _ => 0.01m
        };
        if (agent.Rating is { } rating) score += Math.Clamp(rating / 200m, 0m, 0.05m);
        return Math.Clamp(score, 0m, 0.99m);
    }

    private static string DeduplicationKey(AvailableAgent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.AgentId)) return $"id:{agent.AgentId.Trim()}";
        if (Uri.TryCreate(agent.RepositoryUrl, UriKind.Absolute, out var repository))
            return $"repo:{repository.GetLeftPart(UriPartial.Path).TrimEnd('/')}";
        return $"name:{agent.Publisher.Trim()}:{agent.Name.Trim()}";
    }

    private static AvailableAgent Consolidate(IGrouping<string, AvailableAgent> group)
    {
        var ordered = group.OrderBy(x => SourcePriority(x.Source)).ThenByDescending(x => x.Score).ToList();
        var primary = ordered[0];
        return primary with
        {
            AlternateSources = ordered.Skip(1).Select(x => x.Source).Distinct().ToArray(),
            Capabilities = ordered.SelectMany(x => x.Capabilities).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Score = ordered.Max(x => x.Score)
        };
    }

    internal static int SourcePriority(AgentCatalogSource source) => source switch
    {
        AgentCatalogSource.Installed => 0,
        AgentCatalogSource.LocalDirectory => 1,
        AgentCatalogSource.FirstPartyCatalog => 2,
        AgentCatalogSource.Marketplace => 3,
        _ => 9
    };

    private static AgentCatalogSource ParseSource(string reference)
    {
        var separator = reference.IndexOf(':');
        var prefix = separator > 0 ? reference[..separator] : string.Empty;
        return prefix switch
        {
            "installed" => AgentCatalogSource.Installed,
            "local" => AgentCatalogSource.LocalDirectory,
            "first-party" => AgentCatalogSource.FirstPartyCatalog,
            "marketplace" => AgentCatalogSource.Marketplace,
            _ => throw new ArgumentException("The agent reference is invalid.", nameof(reference))
        };
    }
}

public sealed class InstalledAgentCatalogProvider(CSweetDbContext db) : IAgentCatalogProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public AgentCatalogSource Source => AgentCatalogSource.Installed;

    public async Task<AgentCatalogProviderResult> SearchAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!organizationId.HasValue)
            return new([], new(Source, true, "Choose an organization to include installed agents."));

        var businessId = organizationId.Value.ToString("D");
        var installations = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion)!.ThenInclude(x => x!.PackageSource)
            .Include(x => x.Grant)
            .Where(x => x.BusinessId == businessId && x.RevisionStatus == PluginRevisionStatus.Active)
            .ToListAsync(cancellationToken);
        var agents = installations
            .Where(x => x.PackageVersion is { PluginKind: PluginKind.Agent })
            .Select(Map)
            .ToArray();
        return new(agents, new(Source, true));
    }

    public async Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default)
    {
        if (!organizationId.HasValue || !TryGuid(agentReference, "installed", out var id)) return null;
        var businessId = organizationId.Value.ToString("D");
        var installation = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion)!.ThenInclude(x => x!.PackageSource)
            .Include(x => x.Grant)
            .SingleOrDefaultAsync(x => x.Id == id && x.BusinessId == businessId &&
                x.RevisionStatus == PluginRevisionStatus.Active, cancellationToken);
        return installation?.PackageVersion is { PluginKind: PluginKind.Agent } ? Map(installation) : null;
    }

    private static AvailableAgent Map(AgentInstallation installation)
    {
        var package = installation.PackageVersion!;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, JsonOptions) ?? new();
        return new(
            $"installed:{installation.Id:N}",
            package.AgentId,
            AgentCatalogSource.Installed,
            [],
            installation.IsEnabled ? AgentAvailabilityState.InstalledEnabled : AgentAvailabilityState.InstalledDisabled,
            installation.Id,
            package.AgentName,
            manifest.Catalog.Summary ?? $"Installed {package.AgentName} agent.",
            package.PublisherName,
            manifest.Catalog.Category ?? "Installed",
            manifest.Catalog.RoleAliases,
            manifest.Catalog.Keywords,
            ReadList(installation.Grant?.ProvidedCapabilitiesJson),
            null,
            null,
            null,
            0,
            manifest.Catalog.DocumentationUrl,
            package.PackageSource?.Host == "github.com" ? package.PackageSource.RepositoryUrl : null,
            0.9m,
            "Organization approved");
    }

    private static IReadOnlyList<string> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static bool TryGuid(string reference, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return reference.StartsWith($"{prefix}:", StringComparison.Ordinal) &&
               Guid.TryParseExact(reference[(prefix.Length + 1)..], "N", out id);
    }
}

public sealed class FirstPartyAgentCatalogProvider(IOptions<MarketplaceOptions> options) : IAgentCatalogProvider
{
    public AgentCatalogSource Source => AgentCatalogSource.FirstPartyCatalog;

    public Task<AgentCatalogProviderResult> SearchAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var agents = options.Value.FirstPartyAgents
            .Where(Valid)
            .Select(Map)
            .ToArray();
        return Task.FromResult(new AgentCatalogProviderResult(agents, new(Source, true)));
    }

    public Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryGuid(agentReference, "first-party", out var id)) return Task.FromResult<AvailableAgent?>(null);
        return Task.FromResult(options.Value.FirstPartyAgents.Where(Valid).Where(x => x.Id == id).Select(Map).FirstOrDefault());
    }

    private static bool Valid(FirstPartyMarketplaceAgentOptions item) =>
        item.Id != Guid.Empty && !string.IsNullOrWhiteSpace(item.Name) &&
        Uri.TryCreate(item.RepositoryUrl, UriKind.Absolute, out _);

    private static AvailableAgent Map(FirstPartyMarketplaceAgentOptions item) => new(
        $"first-party:{item.Id:N}",
        NullIfWhiteSpace(item.AgentId),
        AgentCatalogSource.FirstPartyCatalog,
        [],
        string.Equals(item.Availability, "Planned", StringComparison.OrdinalIgnoreCase)
            ? AgentAvailabilityState.Planned
            : AgentAvailabilityState.AvailableToInstall,
        null,
        item.Name,
        item.Summary,
        "C-Sweet",
        item.Category,
        item.RoleAliases,
        item.Keywords,
        item.Capabilities,
        null,
        "USD",
        null,
        0,
        string.IsNullOrWhiteSpace(item.DocumentationUrl) ? $"{item.RepositoryUrl}#readme" : item.DocumentationUrl,
        item.RepositoryUrl,
        item.IsFeatured ? 0.8m : 0.7m,
        "C-Sweet first party");

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static bool TryGuid(string reference, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return reference.StartsWith($"{prefix}:", StringComparison.Ordinal) &&
               Guid.TryParseExact(reference[(prefix.Length + 1)..], "N", out id);
    }
}

public sealed class MarketplaceAgentCatalogProvider(IMarketplaceDiscoveryService marketplace) : IAgentCatalogProvider
{
    public AgentCatalogSource Source => AgentCatalogSource.Marketplace;

    public async Task<AgentCatalogProviderResult> SearchAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await marketplace.SearchAsync(new MarketplaceDiscoveryQuery(
            query.SearchString ?? query.Role,
            query.Category,
            query.RequiredCapabilities?.FirstOrDefault(),
            null,
            query.MaximumPrice,
            query.Sort,
            Math.Clamp(query.Limit * 3, 1, 100)), cancellationToken);
        return new(
            result.Items.Where(x => !x.IsFirstParty).Select(Map).ToArray(),
            new(Source, result.IsOnline, result.UnavailableReason));
    }

    public async Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryGuid(agentReference, "marketplace", out var id)) return null;
        var result = await marketplace.SearchAsync(new MarketplaceDiscoveryQuery(Take: 100), cancellationToken);
        return result.Items.Where(x => !x.IsFirstParty && x.Id == id).Select(Map).FirstOrDefault();
    }

    private static AvailableAgent Map(MarketplaceAgentResponse item) => new(
        $"marketplace:{item.Id:N}",
        null,
        AgentCatalogSource.Marketplace,
        [],
        AgentAvailabilityState.AvailableToInstall,
        null,
        item.Name,
        item.Summary,
        item.PublisherName,
        item.Category,
        [item.Name],
        [],
        item.Capabilities,
        item.PriceInCents / 100m,
        item.Currency,
        item.Rating,
        item.RatingCount,
        item.DocumentationUrl,
        item.RepositoryUrl,
        item.Rating is { } rating ? Math.Clamp(rating / 10m, 0m, 0.99m) : 0.6m,
        "Marketplace publisher");

    private static bool TryGuid(string reference, string prefix, out Guid id)
    {
        id = Guid.Empty;
        return reference.StartsWith($"{prefix}:", StringComparison.Ordinal) &&
               Guid.TryParseExact(reference[(prefix.Length + 1)..], "N", out id);
    }
}

public sealed class LocalDirectoryAgentCatalogProvider(
    IHostEnvironment environment,
    IOptions<AgentCatalogOptions> options,
    IPluginManifestReader manifestReader) : IAgentCatalogProvider, ILocalAgentSourceArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ExcludedDirectories =
        new([".git", ".vs", "bin", "obj"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedFiles =
        new([".env", "secrets.json"], StringComparer.OrdinalIgnoreCase);
    public AgentCatalogSource Source => AgentCatalogSource.LocalDirectory;

    public async Task<AgentCatalogProviderResult> SearchAsync(
        Guid? organizationId,
        AvailableAgentSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var root = RootPath();
        if (!Directory.Exists(root))
            return new([], new(Source, false, $"Local agent directory is not present at the configured application path."));

        var agents = new List<AvailableAgent>();
        var failures = 0;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var agent = await ReadAsync(root, directory, cancellationToken);
                if (agent is not null) agents.Add(agent);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AgentImportPreviewException)
            {
                failures++;
            }
        }
        var message = failures == 0 ? null : $"{failures} local agent folder(s) were invalid and ignored.";
        return new(agents, new(Source, true, message));
    }

    public async Task<AvailableAgent?> ResolveAsync(
        Guid? organizationId,
        string agentReference,
        CancellationToken cancellationToken = default)
    {
        if (!agentReference.StartsWith("local:", StringComparison.Ordinal)) return null;
        var result = await SearchAsync(organizationId, new(Limit: 100), cancellationToken);
        return result.Agents.FirstOrDefault(x => string.Equals(x.AgentReference, agentReference, StringComparison.Ordinal));
    }

    public async Task<LocalAgentSourceArchive> CreateArchiveAsync(
        string agentReference,
        CancellationToken cancellationToken = default)
    {
        if (!agentReference.StartsWith("local:", StringComparison.Ordinal))
            throw new AgentImportPreviewException("The local agent reference is invalid.");
        var root = RootPath();
        if (!Directory.Exists(root))
            throw new AgentImportPreviewException("The configured local agent directory is unavailable.");
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AvailableAgent? agent;
            try { agent = await ReadAsync(root, directory, cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or AgentImportPreviewException)
            {
                continue;
            }
            if (agent is null || !string.Equals(agent.AgentReference, agentReference, StringComparison.Ordinal))
                continue;

            var files = EnumerateIncludedFiles(directory).OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in files)
                {
                    var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var entryStream = entry.Open();
                    await using var input = File.OpenRead(file.FullPath);
                    await input.CopyToAsync(entryStream, cancellationToken);
                }
            }
            var digest = agentReference[(agentReference.LastIndexOf(':') + 1)..];
            return new($"{SanitizeFileName(agent.AgentId ?? agent.Name)}-{digest[..12]}.zip", output.ToArray(), digest);
        }
        throw new AgentImportPreviewException(
            "The local agent source changed or was removed after catalog discovery. Refresh the catalog and try again.");
    }

    internal string RootPath()
    {
        var configured = options.Value.LocalDirectoryPath;
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }

    private async Task<AvailableAgent?> ReadAsync(string root, string directory, CancellationToken token)
    {
        EnsureUnderRoot(root, directory);
        RejectReparsePoint(directory);
        var manifestPath = Path.Combine(directory, "csweet-plugin.json");
        if (!File.Exists(manifestPath)) return null;
        RejectReparsePoint(manifestPath);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, token);
        if (manifestBytes.Length > 1024 * 1024) throw new AgentImportPreviewException("Plugin manifest exceeds the 1 MB limit.");
        var envelope = manifestReader.Read(manifestBytes, "csweet-plugin.json");
        // A development workspace can contain agent, service, SDK, and application
        // repositories side by side. Non-agent manifests are valid workspace entries,
        // but they do not belong in the agent catalog.
        if (!string.Equals(envelope.Kind, "agent", StringComparison.Ordinal)) return null;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(envelope.ManifestJson, JsonOptions)
            ?? throw new JsonException("Plugin manifest is empty.");
        AgentImportPreviewService.ValidateManifest(manifest);
        var project = Path.GetFullPath(Path.Combine(directory, manifest.Runtime.ProjectPath!));
        EnsureUnderRoot(directory, project);
        if (!File.Exists(project)) throw new AgentImportPreviewException("The declared runtime project does not exist.");
        var digest = await DigestAsync(directory, token);
        var reference = $"local:{Uri.EscapeDataString(manifest.Id)}:{digest}";
        return new(
            reference,
            manifest.Id,
            AgentCatalogSource.LocalDirectory,
            [],
            AgentAvailabilityState.AvailableToInstall,
            null,
            manifest.Name,
            manifest.Catalog.Summary ?? $"Local {manifest.Name} agent.",
            manifest.Publisher.Name,
            manifest.Catalog.Category ?? "Local",
            manifest.Catalog.RoleAliases.Count > 0 ? manifest.Catalog.RoleAliases : [manifest.Name],
            manifest.Catalog.Keywords,
            manifest.Provides.Select(x => x.Name).ToArray(),
            null,
            null,
            null,
            0,
            manifest.Catalog.DocumentationUrl,
            null,
            0.75m,
            "User-provided local source");
    }

    private async Task<string> DigestAsync(string directory, CancellationToken token)
    {
        var files = EnumerateIncludedFiles(directory).OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        if (files.Length > options.Value.MaximumFileCount)
            throw new AgentImportPreviewException("Local agent source contains too many files.");
        long total = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            RejectReparsePoint(file.FullPath);
            var bytes = await File.ReadAllBytesAsync(file.FullPath, token);
            total = checked(total + bytes.Length);
            if (total > options.Value.MaximumSourceSizeMb * 1024L * 1024L)
                throw new AgentImportPreviewException("Local agent source exceeds the configured size limit.");
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData([0]);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> EnumerateIncludedFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                RejectReparsePoint(child);
                if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (ExcludedFiles.Contains(name) || name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)) continue;
                yield return (file, Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new AgentImportPreviewException("Local agent source cannot contain symbolic links or reparse points.");
    }

    private static void EnsureUnderRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new AgentImportPreviewException("Local agent source escapes the configured directory.");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "local-agent" : sanitized;
    }
}

public sealed class AgentCatalogWarmupService(IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IAgentCatalogService>();
        await catalog.GetAvailableAgentsAsync(null, new(Limit: 1), stoppingToken);
    }
}
