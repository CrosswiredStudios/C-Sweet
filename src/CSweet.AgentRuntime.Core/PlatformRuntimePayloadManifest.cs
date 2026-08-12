using System.Security.Cryptography;
using System.Text.Json;
using CSweet.AgentRuntime.Abstractions;

namespace CSweet.AgentRuntime.Core;

/// <summary>
/// Loads the immutable platform payload installed beside RuntimeHost. The manifest never grants
/// additional behavior: it only binds a known provider to fixed package files and certification
/// metadata after every declared file has passed its SHA-256 check.
/// </summary>
public static class PlatformRuntimePayloadManifest
{
    private const long MaximumManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void ApplyIfConfigured(
        PlatformIsolationBackendOptions options,
        IsolationProviderDescriptor expectedProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(expectedProvider);
        if (string.IsNullOrWhiteSpace(options.PayloadManifestPath)) return;
        if (!Path.IsPathFullyQualified(options.PayloadManifestPath))
            throw new InvalidDataException("The platform payload manifest path must be absolute.");

        var manifestPath = Path.GetFullPath(options.PayloadManifestPath);
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists || manifestInfo.Length is < 2 or > MaximumManifestBytes)
            throw new InvalidDataException("The platform payload manifest is missing or exceeds its size limit.");
        RejectLink(manifestInfo);

        PayloadManifest manifest;
        try
        {
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            manifest = JsonSerializer.Deserialize<PayloadManifest>(stream, JsonOptions)
                ?? throw new InvalidDataException("The platform payload manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The platform payload manifest is malformed.", exception);
        }

        ValidateIdentity(manifest, expectedProvider);
        var packageRoot = manifestInfo.DirectoryName
            ?? throw new InvalidDataException("The platform payload manifest has no package directory.");
        var declaredFiles = ValidateFiles(manifest, packageRoot);
        var helper = ResolveDeclaredFile(packageRoot, manifest.HelperExecutable, declaredFiles);
        var guest = ResolveDeclaredFile(packageRoot, manifest.GuestImage, declaredFiles);
        var signature = ResolveDeclaredFile(packageRoot, manifest.GuestImageSignature, declaredFiles);
        var certificate = ResolveDeclaredFile(packageRoot, manifest.GuestImageSigningCertificate, declaredFiles);
        var evidence = ResolveDeclaredFile(packageRoot, manifest.CertificationEvidence, declaredFiles);

        var guestDigest = Digest(declaredFiles[NormalizeRelativePath(manifest.GuestImage)]);
        var evidenceDigest = Digest(declaredFiles[NormalizeRelativePath(manifest.CertificationEvidence)]);
        if (!string.Equals(guestDigest, NormalizeDigest(manifest.GuestImageDigest), StringComparison.Ordinal) ||
            !string.Equals(evidenceDigest, NormalizeDigest(manifest.CertificationEvidenceDigest), StringComparison.Ordinal))
            throw new InvalidDataException("The platform payload identity digests do not match the declared files.");
        if (string.IsNullOrWhiteSpace(manifest.GuestImageSigningCertificateThumbprint) ||
            string.IsNullOrWhiteSpace(manifest.CertificationSuiteVersion) ||
            manifest.CertifiedAt is null)
            throw new InvalidDataException("The platform payload certification metadata is incomplete.");

        options.HelperExecutablePath = helper;
        options.HelperExecutableDigest = Digest(declaredFiles[NormalizeRelativePath(manifest.HelperExecutable)]);
        options.GuestImagePath = guest;
        options.GuestImageDigest = guestDigest;
        options.GuestImageSignaturePath = signature;
        options.GuestImageSigningCertificatePath = certificate;
        options.GuestImageSigningCertificateThumbprint = manifest.GuestImageSigningCertificateThumbprint.Trim();
        options.BrokerProtocolVersion = Required(manifest.BrokerProtocolVersion, "brokerProtocolVersion");
        options.CertificationSuiteVersion = manifest.CertificationSuiteVersion.Trim();
        options.CertificationEvidencePath = evidence;
        options.CertificationEvidenceDigest = evidenceDigest;
        options.CertifiedAt = manifest.CertifiedAt;
        options.CertificationExpiresAt = manifest.CertificationExpiresAt;
    }

    private static void ValidateIdentity(PayloadManifest manifest, IsolationProviderDescriptor provider)
    {
        if (manifest.SchemaVersion != 1 ||
            !string.Equals(manifest.ProviderId, provider.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProviderVersion, provider.ProviderVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.HostOperatingSystem, provider.HostOperatingSystem, StringComparison.Ordinal) ||
            !string.Equals(manifest.HostArchitecture, provider.HostArchitecture, StringComparison.Ordinal))
            throw new InvalidDataException("The platform payload manifest is not for this exact provider build and host.");
    }

    private static Dictionary<string, PayloadFile> ValidateFiles(PayloadManifest manifest, string packageRoot)
    {
        if (manifest.Files is null || manifest.Files.Count is < 5 or > 1000)
            throw new InvalidDataException("The platform payload file list is invalid.");
        var files = new Dictionary<string, PayloadFile>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var relative = NormalizeRelativePath(file.Path);
            if (!files.TryAdd(relative, file))
                throw new InvalidDataException($"The platform payload contains a duplicate file entry: {relative}");
            var expected = NormalizeDigest(file.Sha256);
            var path = ResolveSafeFile(packageRoot, relative);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var actual = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(stream))}";
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(expected)))
                throw new InvalidDataException($"The platform payload integrity check failed: {relative}");
        }
        return files;
    }

    private static string ResolveDeclaredFile(
        string packageRoot,
        string value,
        IReadOnlyDictionary<string, PayloadFile> declaredFiles)
    {
        var relative = NormalizeRelativePath(value);
        if (!declaredFiles.ContainsKey(relative))
            throw new InvalidDataException($"A required platform payload file is not declared: {relative}");
        return ResolveSafeFile(packageRoot, relative);
    }

    private static string ResolveSafeFile(string packageRoot, string relative)
    {
        var root = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException("A platform payload path escaped its package directory.");
        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (!info.Exists && !Directory.Exists(current))
                throw new InvalidDataException($"A declared platform payload file is missing: {relative}");
            RejectLink(info);
        }
        if (!File.Exists(candidate))
            throw new InvalidDataException($"A declared platform payload file is missing: {relative}");
        return candidate;
    }

    private static void RejectLink(FileSystemInfo info)
    {
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw new InvalidDataException("Platform payload symbolic links are not allowed.");
    }

    private static string NormalizeRelativePath(string value)
    {
        var relative = Required(value, "payload file path").Replace('\\', '/');
        if (Path.IsPathFullyQualified(relative) || relative.StartsWith("/", StringComparison.Ordinal) ||
            relative.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl)))
            throw new InvalidDataException("A platform payload file path is invalid.");
        return relative;
    }

    private static string NormalizeDigest(string value)
    {
        var digest = Required(value, "SHA-256 digest");
        if (digest.Length == 64) digest = $"sha256:{digest}";
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new InvalidDataException("A platform payload digest is not lowercase SHA-256.");
        return digest;
    }

    private static string Digest(PayloadFile file) => NormalizeDigest(file.Sha256);

    private static string Required(string value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidDataException($"The platform payload {name} is required.");

    internal sealed class PayloadManifest
    {
        public int SchemaVersion { get; set; }
        public string ProviderId { get; set; } = string.Empty;
        public string ProviderVersion { get; set; } = string.Empty;
        public string HostOperatingSystem { get; set; } = string.Empty;
        public string HostArchitecture { get; set; } = string.Empty;
        public string HelperExecutable { get; set; } = string.Empty;
        public string GuestImage { get; set; } = string.Empty;
        public string GuestImageDigest { get; set; } = string.Empty;
        public string GuestImageSignature { get; set; } = string.Empty;
        public string GuestImageSigningCertificate { get; set; } = string.Empty;
        public string GuestImageSigningCertificateThumbprint { get; set; } = string.Empty;
        public string BrokerProtocolVersion { get; set; } = "1.0";
        public string CertificationSuiteVersion { get; set; } = string.Empty;
        public string CertificationEvidence { get; set; } = string.Empty;
        public string CertificationEvidenceDigest { get; set; } = string.Empty;
        public DateTimeOffset? CertifiedAt { get; set; }
        public DateTimeOffset? CertificationExpiresAt { get; set; }
        public List<PayloadFile>? Files { get; set; }
    }

    internal sealed class PayloadFile
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }
}
