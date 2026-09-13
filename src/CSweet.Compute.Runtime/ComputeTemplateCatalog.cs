using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

public sealed record ComputeTemplateCatalogEntry(ComputeTemplate Template, string ImagePath,
    SignedComputeImageCertification Certification);
public sealed record ComputeTemplateCatalogDocument(int Version, IReadOnlyList<ComputeTemplateCatalogEntry> Templates);

/// <summary>Installer-owned template selection. Dispatches never supply image paths or release keys.</summary>
public sealed class ComputeTemplateCatalog
{
    private readonly Dictionary<string, ComputeTemplateCatalogEntry> entries;
    private readonly string publicKey, providerId, providerVersion, runtimeDirectory;
    private readonly HashSet<string> runtimeFiles;
    private readonly Action<string> protection;
    private readonly TimeProvider clock;

    private ComputeTemplateCatalog(Dictionary<string, ComputeTemplateCatalogEntry> entries, string publicKey,
        string providerId, string providerVersion, string runtimeDirectory, IReadOnlySet<string> runtimeFiles,
        Action<string> protection, TimeProvider clock)
    {
        this.entries = entries; this.publicKey = publicKey; this.providerId = providerId;
        this.providerVersion = providerVersion; this.runtimeDirectory = runtimeDirectory;
        this.runtimeFiles = new(runtimeFiles, StringComparer.Ordinal); this.protection = protection; this.clock = clock;
    }

    // Return detached values: a verifier or caller cannot mutate this catalog's certified template sets.
    public IReadOnlyDictionary<string, ComputeTemplate> Templates => entries.ToDictionary(x => x.Key,
        x => x.Value.Template with { Features = x.Value.Template.Features.ToHashSet(StringComparer.Ordinal) }, StringComparer.Ordinal);

    public static Task<ComputeTemplateCatalog> ReadAsync(string path, string pinnedReleasePublicKey,
        string providerId, string providerVersion, string runtimeDirectory, IReadOnlySet<string> requiredRuntimeFiles,
        TimeProvider clock, CancellationToken token = default) => ReadAsync(path, pinnedReleasePublicKey,
            providerId, providerVersion, runtimeDirectory, requiredRuntimeFiles, clock, WindowsComputeProtectedPaths.Verify, token);

    internal static async Task<ComputeTemplateCatalog> ReadAsync(string path, string pinnedReleasePublicKey,
        string providerId, string providerVersion, string runtimeDirectory, IReadOnlySet<string> requiredRuntimeFiles,
        TimeProvider clock, Action<string> protection, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(runtimeDirectory) ||
            !ComputeSpecification.Identifier(providerId) || string.IsNullOrWhiteSpace(providerVersion) ||
            requiredRuntimeFiles is not { Count: > 0 and <= 512 })
            throw new InvalidDataException("Installed template catalog settings are invalid.");
        protection(path); protection(runtimeDirectory);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 1048576) throw new InvalidDataException("Template catalog exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
        var document = JsonSerializer.Deserialize<ComputeTemplateCatalogDocument>(bytes, ComputeProtocol.Json);
        if (document is not { Version: 1, Templates.Count: > 0 and <= 16 })
            throw new InvalidDataException("Template catalog version or entry count is invalid.");
        var entries = new Dictionary<string, ComputeTemplateCatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in document.Templates)
        {
            if (entry is not { Template: not null, Certification: not null } ||
                string.IsNullOrWhiteSpace(entry.ImagePath) || !Path.IsPathFullyQualified(entry.ImagePath))
                throw new InvalidDataException("Template catalog entry is invalid.");
            var evidence = ComputeImageCertificationVerifier.Verify(entry.Certification, pinnedReleasePublicKey,
                providerId, providerVersion, entry.Template, clock.GetUtcNow());
            if (!requiredRuntimeFiles.SetEquals(evidence.RuntimeFiles.Keys) ||
                evidence.RuntimeFiles.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != evidence.RuntimeFiles.Count)
                throw new UnauthorizedAccessException("Template certification omits installed runtime files.");
            protection(entry.ImagePath);
            if (!entries.TryAdd(entry.Template.Id, entry)) throw new InvalidDataException("Template IDs must be unique.");
        }
        return new(entries, pinnedReleasePublicKey, providerId, providerVersion, Path.GetFullPath(runtimeDirectory),
            requiredRuntimeFiles, protection, clock);
    }

    /// <summary>Read-only installer acceptance: certification must cover this process's complete flat runtime payload.</summary>
    public async Task ValidateInstallationAsync(string installedRuntimeDirectory, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(installedRuntimeDirectory) ||
            !string.Equals(Path.GetFullPath(installedRuntimeDirectory).TrimEnd(Path.DirectorySeparatorChar),
                runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Certification must cover the installed provider runtime directory.");
        protection(runtimeDirectory);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFileSystemEntries(runtimeDirectory))
        {
            token.ThrowIfCancellationRequested(); protection(path);
            if (Directory.Exists(path) || !actual.Add(Path.GetFileName(path)) || actual.Count > 512)
                throw new UnauthorizedAccessException("Installed runtime topology is not completely certified.");
        }
        if (!runtimeFiles.SetEquals(actual)) throw new UnauthorizedAccessException("Installed runtime files differ from the certified manifest.");
        foreach (var entry in entries.Values.OrderBy(x => x.Template.Id, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using var payload = await ComputeCertifiedPayload.OpenAsync(entry.Certification, publicKey, providerId,
                providerVersion, entry.Template, clock.GetUtcNow(), entry.ImagePath, runtimeDirectory, runtimeFiles, protection, token);
        }
    }
    public Task<ComputeCertifiedPayload> OpenAsync(VerifiedComputeDispatch dispatch, CancellationToken token)
    {
        if (dispatch.Authorization is not { Mode: ComputeDispatchMode.Execute, Action: InfrastructureActions.Provision } ||
            dispatch.Authorization.ProviderId != providerId || dispatch.Template is not { } template ||
            !entries.TryGetValue(template.Id, out var entry))
            throw new UnauthorizedAccessException("The dispatch cannot select a certified template payload.");
        // Revalidate expiry and hash/lock actual files on every use, including after catalog startup.
        return ComputeCertifiedPayload.OpenAsync(entry.Certification, publicKey, providerId, providerVersion,
            template, clock.GetUtcNow(), entry.ImagePath, runtimeDirectory, runtimeFiles, protection, token);
    }
}
