using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

/// <summary>Previews a prebuilt .cspb through the same artifact gate as fleet builds. No installation or grants are created.</summary>
public sealed class PluginBundleImportService(
    CSweetDbContext db, IPluginManifestReader manifestReader, IAgentArtifactStore artifacts,
    AgentImportPreviewService previews, IAuditEventWriter audit,
    IOptions<AgentRuntimeManagerOptions> options) : IPluginBundleImportService
{
    public const long MaximumBundleBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentImportPreviewResponse> PreviewAsync(Stream bundle, string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 ||
            fileName != Path.GetFileName(fileName) || fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.Any(char.IsControl) || !fileName.EndsWith(".cspb", StringComparison.OrdinalIgnoreCase))
            throw new AgentImportPreviewException("Choose a C-Sweet plugin bundle (.cspb).");
        var root = options.Value.SourceArchiveStorePath;
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ||
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) == Path.GetPathRoot(Path.GetFullPath(root)))
            throw new InvalidOperationException("The bundle staging store must be an absolute non-root directory.");
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, $"{Guid.NewGuid():N}.upload");
        try
        {
            await using var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await bundle.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaximumBundleBytes) throw new AgentImportPreviewException("Plugin bundle exceeds the 512 MB limit.");
                hash.AppendData(buffer, 0, read);
                await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var artifactBytes = await ReadEntryAsync(staged, "artifact.json", 1024 * 1024, cancellationToken);
            var artifact = JsonSerializer.Deserialize<BundleMetadata>(artifactBytes, JsonOptions)
                ?? throw new AgentImportPreviewException("Bundle metadata is missing.");
            if (artifact.FormatVersion != "1.0" || artifact.OperatingSystem != "linux" || artifact.Architecture is not ("x64" or "arm64"))
                throw new AgentImportPreviewException("Plugin bundles must use artifact format 1.0 for Linux x64 or arm64.");
            var manifestBytes = await ReadEntryAsync(staged, "payload/csweet-plugin.json", 1024 * 1024, cancellationToken);
            var envelope = manifestReader.Read(manifestBytes, "csweet-plugin.json");
            if (envelope.Kind is not ("service" or "connector"))
                throw new AgentImportPreviewException("Agent packages belong in Installed Agents. Choose a service or connector plugin.");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(envelope.ManifestJson, JsonOptions)
                ?? throw new AgentImportPreviewException("Plugin manifest is empty.");
            AgentImportPreviewService.ValidateManifest(manifest);

            // The artifact store checks all paths, duplicates, links, modes, entrypoint and size,
            // and seals the exact bytes whose digest the user will review. It never executes them.
            staged.Position = 0;
            var reference = await artifacts.ImportAsync(staged, new("sha256:" + hex, MaximumBundleBytes,
                artifact.FormatVersion, artifact.OperatingSystem, artifact.Architecture,
                JsonSerializer.Serialize(new { source = "plugin-bundle-upload", fileName, bundleDigest = hex, manifest.Id, manifest.Version })), cancellationToken);
            await previews.StageBundleContributionsAsync(manifest,
                (resource, maximum, token) => ReadEntryAsync(staged, "payload/" + resource, maximum, token), cancellationToken);
            var sourceUrl = $"upload://{hex}/plugin.cspb";
            var source = await db.AgentPackageSources.SingleOrDefaultAsync(x => x.RepositoryUrl == sourceUrl, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (source is null)
            {
                source = new AgentPackageSource
                {
                    Id = Guid.NewGuid(), RepositoryUrl = sourceUrl, Host = "upload", RepositoryOwner = "local",
                    RepositoryName = manifest.Id, DefaultBranch = "bundle", SourceType = "PluginBundle", CreatedAt = now, UpdatedAt = now
                };
                db.AgentPackageSources.Add(source);
            }
            var version = await db.AgentPackageVersions.SingleOrDefaultAsync(x => x.PackageSourceId == source.Id, cancellationToken);
            var warnings = AgentImportPreviewService.CreateWarnings(manifest);
            if (version is null)
            {
                version = new AgentPackageVersion
                {
                    Id = Guid.NewGuid(), PackageSourceId = source.Id, CommitSha = hex[..40],
                    ManifestDigest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                    CapabilityDescriptorsDigest = AgentImportPreviewService.CapabilityDescriptorsDigest(manifest),
                    ManifestJson = envelope.ManifestJson, ManifestFileName = envelope.ManifestFileName,
                    PluginKind = envelope.Kind == "service" ? PluginKind.Service : PluginKind.Connector,
                    AgentId = manifest.Id, AgentName = manifest.Name, Version = manifest.Version,
                    PublisherId = manifest.Publisher.Id, PublisherName = manifest.Publisher.Name,
                    RuntimeType = manifest.Runtime.Type, ProjectPath = manifest.Runtime.ProjectPath,
                    TargetFramework = manifest.Runtime.TargetFramework, DefaultActivationMode = manifest.Runtime.DefaultActivationMode,
                    WarningsJson = JsonSerializer.Serialize(warnings, JsonOptions),
                    // Built describes artifact readiness. Only an approved installation creates execution grants.
                    Status = AgentPackageVersionStatus.Built, PackageDigest = hex, ArtifactSignature = reference.Signature,
                    ArtifactFormatVersion = reference.FormatVersion, ArtifactOperatingSystem = reference.OperatingSystem,
                    ArtifactArchitecture = reference.Architecture, BuiltAt = now, ImportedAt = now
                };
                db.AgentPackageVersions.Add(version);
            }
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("plugin-import.bundle.previewed", nameof(AgentPackageVersion), version.Id,
                $"Verified plugin bundle {manifest.Id} {manifest.Version}; SHA-256 {hex}.", cancellationToken: cancellationToken);
            return AgentImportPreviewService.ToResponse(version, source, manifest, warnings) with { BundleDigest = hex };
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or EndOfStreamException)
        {
            throw new AgentImportPreviewException($"Plugin bundle is invalid: {exception.Message}", exception);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<byte[]> ReadEntryAsync(Stream bundle, string name, int maximumBytes, CancellationToken token)
    {
        bundle.Position = 0;
        using var reader = new TarReader(bundle, leaveOpen: true);
        var count = 0;
        while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
        {
            if (++count > 10_000) throw new AgentImportPreviewException("Plugin bundle contains too many files.");
            if (entry.Name != name) continue;
            if (entry.Length > maximumBytes || entry.DataStream is null ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new AgentImportPreviewException($"Bundle resource '{name}' is invalid or oversized.");
            using var content = new MemoryStream();
            await entry.DataStream.CopyToAsync(content, token);
            return content.ToArray();
        }
        throw new AgentImportPreviewException($"Plugin bundle is missing '{name}'.");
    }

    private sealed record BundleMetadata(string FormatVersion, string OperatingSystem, string Architecture);
}
