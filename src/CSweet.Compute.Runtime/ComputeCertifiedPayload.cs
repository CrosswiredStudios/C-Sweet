using System.Security.Cryptography;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

/// <summary>Keeps certified files open without write/delete sharing while a backend consumes them.</summary>
public sealed class ComputeCertifiedPayload : IDisposable
{
    private readonly List<FileStream> files;
    public string ImagePath { get; }

    private ComputeCertifiedPayload(string imagePath, List<FileStream> files)
    { ImagePath = imagePath; this.files = files; }

    public static Task<ComputeCertifiedPayload> OpenAsync(SignedComputeImageCertification envelope,
        string pinnedPublicKey, string providerId, string providerVersion, ComputeTemplate expected,
        DateTimeOffset now, string imagePath, string runtimeDirectory, IReadOnlySet<string> requiredRuntimeFiles,
        CancellationToken cancellationToken = default) => OpenAsync(envelope, pinnedPublicKey, providerId,
            providerVersion, expected, now, imagePath, runtimeDirectory, requiredRuntimeFiles,
            WindowsComputeProtectedPaths.Verify, cancellationToken);

    // Tests exercise file hashing/locking independently of administrator-owned deployment ACLs.
    internal static async Task<ComputeCertifiedPayload> OpenAsync(SignedComputeImageCertification envelope,
        string pinnedPublicKey, string providerId, string providerVersion, ComputeTemplate expected,
        DateTimeOffset now, string imagePath, string runtimeDirectory, IReadOnlySet<string> requiredRuntimeFiles,
        Action<string> verifyProtectedPath, CancellationToken cancellationToken = default)
    {
        var certificate = ComputeImageCertificationVerifier.Verify(envelope, pinnedPublicKey, providerId,
            providerVersion, expected, now);
        // This set comes from the provider's installed payload manifest, never from the caller's request.
        // Exact coverage prevents a signed but incomplete file list from omitting executable dependencies.
        if (requiredRuntimeFiles.Count == 0 || !requiredRuntimeFiles.SetEquals(certificate.RuntimeFiles.Keys) ||
            certificate.RuntimeFiles.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != certificate.RuntimeFiles.Count)
            throw new UnauthorizedAccessException("Certification must cover the complete provider payload.");
        if (!Path.IsPathFullyQualified(imagePath) || !Path.IsPathFullyQualified(runtimeDirectory))
            throw new UnauthorizedAccessException("Certified payload paths must be absolute.");
        var image = Path.GetFullPath(imagePath);
        var directory = Path.GetFullPath(runtimeDirectory);
        verifyProtectedPath(directory);
        var handles = new List<FileStream>();
        try
        {
            await VerifyFileAsync(image, certificate.Template.ImageDigest);
            foreach (var entry in certificate.RuntimeFiles.OrderBy(x => x.Key, StringComparer.Ordinal))
                await VerifyFileAsync(Path.Combine(directory, entry.Key), entry.Value);
            return new ComputeCertifiedPayload(image, handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }

        async Task VerifyFileAsync(string path, string digest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            verifyProtectedPath(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            handles.Add(stream);
            var actual = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!StringComparer.Ordinal.Equals(actual, digest))
                throw new UnauthorizedAccessException("Certified payload content has changed.");
            stream.Position = 0;
        }
    }

    public void Dispose()
    {
        foreach (var file in files) file.Dispose();
        files.Clear();
    }
}
