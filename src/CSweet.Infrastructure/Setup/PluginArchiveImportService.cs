using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class PluginArchiveImportService(
    CSweetDbContext db,
    IPluginManifestReader manifestReader,
    IAuditEventWriter audit,
    IOptions<AgentRuntimeManagerOptions> runtimeOptions) : IPluginArchiveImportService
{
    private const int MaximumArchiveBytes = 100 * 1024 * 1024;
    private const int MaximumEntries = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentImportPreviewResponse> PreviewSourceArchiveAsync(
        Stream archiveStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new AgentImportPreviewException("Source archive must be a ZIP file.");
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await archiveStream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (bytes.Length + read > MaximumArchiveBytes)
                throw new AgentImportPreviewException("Source archive exceeds the 100 MB limit.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        bytes.Position = 0;
        byte[] manifestBytes;
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > MaximumEntries) throw new AgentImportPreviewException("Source archive contains too many files.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long uncompressed = 0;
            ZipArchiveEntry? manifestEntry = null;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (path.StartsWith('/') || Path.IsPathRooted(path) || segments.Contains("..", StringComparer.Ordinal))
                    throw new AgentImportPreviewException("Source archive contains a path traversal entry.");
                if (!paths.Add(path)) throw new AgentImportPreviewException("Source archive contains duplicate or case-colliding paths.");
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000) throw new AgentImportPreviewException("Source archive cannot contain symbolic links.");
                uncompressed = checked(uncompressed + entry.Length);
                if (uncompressed > MaximumArchiveBytes) throw new AgentImportPreviewException("Expanded source archive exceeds the 100 MB limit.");
                if (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > 100)
                    throw new AgentImportPreviewException("Source archive contains a suspicious compression ratio.");
                if (string.Equals(path, "csweet-plugin.json", StringComparison.Ordinal)) manifestEntry = entry;
            }
            if (manifestEntry is null) throw new AgentImportPreviewException("Source archive must contain csweet-plugin.json at its root.");
            if (manifestEntry.Length > 1024 * 1024) throw new AgentImportPreviewException("Plugin manifest exceeds the 1 MB limit.");
            await using var input = manifestEntry.Open();
            using var manifestOutput = new MemoryStream();
            await input.CopyToAsync(manifestOutput, cancellationToken);
            manifestBytes = manifestOutput.ToArray();
        }

        PluginManifestEnvelope envelope;
        PluginManifest manifest;
        try
        {
            envelope = manifestReader.Read(manifestBytes, "csweet-plugin.json");
            manifest = JsonSerializer.Deserialize<PluginManifest>(envelope.ManifestJson, JsonOptions) ?? throw new JsonException("Manifest is empty.");
            AgentImportPreviewService.ValidateManifest(manifest);
        }
        catch (Exception exception) when (exception is JsonException or AgentImportPreviewException)
        {
            throw new AgentImportPreviewException($"Plugin manifest is invalid: {exception.Message}", exception);
        }

        var digest = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
        var manifestDigest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var archiveRoot = ValidatedArchiveRoot(runtimeOptions.Value.SourceArchiveStorePath);
        Directory.CreateDirectory(archiveRoot);
        var archivePath = Path.Combine(archiveRoot, $"{digest}.zip");
        if (!File.Exists(archivePath)) await File.WriteAllBytesAsync(archivePath, bytes.ToArray(), cancellationToken);

        var sourceUrl = $"upload://{digest}/{Uri.EscapeDataString(Path.GetFileName(fileName))}";
        var now = DateTimeOffset.UtcNow;
        var source = await db.AgentPackageSources.SingleOrDefaultAsync(x => x.RepositoryUrl == sourceUrl, cancellationToken);
        if (source is null)
        {
            source = new AgentPackageSource
            {
                Id = Guid.NewGuid(), RepositoryUrl = sourceUrl, Host = "upload", RepositoryOwner = "local",
                RepositoryName = Path.GetFileNameWithoutExtension(fileName)[..Math.Min(100, Path.GetFileNameWithoutExtension(fileName).Length)],
                DefaultBranch = "archive", SourceType = "SourceArchive", SourceArchivePath = archivePath,
                CreatedAt = now, UpdatedAt = now
            };
            db.AgentPackageSources.Add(source);
        }
        var commit = digest[..40];
        var version = await db.AgentPackageVersions.SingleOrDefaultAsync(x => x.PackageSourceId == source.Id && x.CommitSha == commit && x.ManifestDigest == manifestDigest, cancellationToken);
        if (version is null)
        {
            version = new AgentPackageVersion
            {
                Id = Guid.NewGuid(), PackageSourceId = source.Id, CommitSha = commit, ManifestDigest = manifestDigest,
                ManifestJson = envelope.ManifestJson, ManifestFileName = envelope.ManifestFileName,
                PluginKind = envelope.Kind == "agent" ? PluginKind.Agent : envelope.Kind == "connector" ? PluginKind.Connector : PluginKind.Service,
                AgentId = manifest.Id, AgentName = manifest.Name, Version = manifest.Version,
                PublisherId = manifest.Publisher.Id, PublisherName = manifest.Publisher.Name,
                RuntimeType = manifest.Runtime.Type, ProjectPath = manifest.Runtime.ProjectPath,
                TargetFramework = manifest.Runtime.TargetFramework, DefaultActivationMode = manifest.Runtime.DefaultActivationMode,
                WarningsJson = "[]", Status = AgentPackageVersionStatus.Previewed, ImportedAt = now
            };
            db.AgentPackageVersions.Add(version);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("plugin-import.archive.previewed", nameof(AgentPackageVersion), version.Id,
                $"Previewed {manifest.Id} {manifest.Version} from source archive {fileName}.", cancellationToken: cancellationToken);
        }

        return new AgentImportPreviewResponse(
            version.Id, sourceUrl, commit, manifestDigest, manifest.Id, manifest.Name, manifest.Version,
            manifest.Publisher.Id, manifest.Publisher.Name, manifest.Runtime.Type, manifest.Runtime.ProjectPath,
            manifest.Runtime.TargetFramework, manifest.Runtime.DefaultActivationMode,
            manifest.Provides.Select(x => x.Name).ToArray(), manifest.Events.Subscribes, manifest.Events.Publishes,
            [], AgentImportPreviewService.WebGrantTokens(manifest), [], version.Status.ToString())
        {
            PluginKind = version.PluginKind.ToString(), ManifestFileName = "csweet-plugin.json",
            RequestedCapabilities = AgentImportPreviewService.GrantRequiredCapabilities(manifest), WebAccess = manifest.WebAccess,
            ConfigurationFields = manifest.Configuration, CredentialBindings = manifest.Credentials
        };
    }

    private static string ValidatedArchiveRoot(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException("The source archive store must be configured as an absolute path.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        if (Path.GetPathRoot(root) == root)
            throw new InvalidOperationException("The source archive store cannot be a filesystem root.");
        return root;
    }
}
