using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Compute.Contracts;
using CSweet.Domain.Compute;

namespace CSweet.Compute.Runtime;

/// <summary>Release evidence, independently signed from dispatch and result authority.</summary>
public static class ComputeImageCertificationVerifier
{
    public const string Purpose = "CSweet.Compute.ImageCertification.v1";
    public static IReadOnlyList<string> RequiredControls { get; } = Array.AsReadOnly(new[]
    {
        "dedicated-kernel", "no-host-filesystem", "explicit-network-policy", "cpu-memory-disk-limits",
        "independent-lease-enforcement", "generation-fencing", "bounded-guest-execution", "persistent-storage-preservation"
    });
    public static byte[] Payload(string json) => Encoding.UTF8.GetBytes(Purpose + "\n" + json);

    public static ComputeImageCertification Verify(SignedComputeImageCertification envelope, string pinnedPublicKey,
        string providerId, string providerVersion, ComputeTemplate expected, DateTimeOffset now)
    {
        if (envelope.CertificateJson is not { Length: > 0 and <= 65536 } || envelope.SignatureBase64 is not { Length: > 0 and <= 512 } ||
            pinnedPublicKey is not { Length: > 0 and <= 1024 }) throw new UnauthorizedAccessException("Compute certification is invalid.");
        try
        {
            using var key = ECDsa.Create(); var bytes = Convert.FromBase64String(pinnedPublicKey);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !key.VerifyData(Payload(envelope.CertificateJson), Convert.FromBase64String(envelope.SignatureBase64), HashAlgorithmName.SHA256))
                throw new UnauthorizedAccessException("Compute certification signature is invalid.");
            var certificate = JsonSerializer.Deserialize<ComputeImageCertification>(envelope.CertificateJson, ComputeProtocol.Json)
                ?? throw new UnauthorizedAccessException("Compute certification is missing.");
            if (certificate.Purpose != Purpose || certificate.ProviderId != providerId || certificate.ProviderVersion != providerVersion ||
                certificate.SuiteVersion != "1" || certificate.IssuedAt > now || certificate.ExpiresAt <= now ||
                certificate.ExpiresAt <= certificate.IssuedAt || certificate.Controls is not { Count: > 0 and <= 64 } ||
                RequiredControls.Except(certificate.Controls, StringComparer.Ordinal).Any() || certificate.RuntimeFiles is not { Count: > 0 and <= 512 } ||
                certificate.Template is not { Features.Count: <= 64, Enabled: true } template ||
                !ComputeSpecification.Identifier(template.Id) || !ComputeSpecification.Identifier(template.OperatingSystem) ||
                !ComputeSpecification.Identifier(template.Architecture) || !expected.Enabled || expected.Features is null || template.Id != expected.Id || template.OperatingSystem != expected.OperatingSystem || template.Architecture != expected.Architecture ||
                template.ImageDigest != expected.ImageDigest || !template.Features.SetEquals(expected.Features) ||
                !Digest(template.ImageDigest) || template.Features.Any(x => !ComputeSpecification.Identifier(x)))
                throw new UnauthorizedAccessException("Compute certification does not cover this provider and template.");
            foreach (var file in certificate.RuntimeFiles)
                if (file.Key.Length is < 1 or > 128 || file.Key is "." or ".." ||
                    file.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || !Digest(file.Value))
                    throw new UnauthorizedAccessException("Compute certification contains an invalid runtime entry.");
            return certificate;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        { throw new UnauthorizedAccessException("Compute certification evidence is invalid.", error); }
    }

    private static bool Digest(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        !value.AsSpan(7).ContainsAnyExcept("0123456789abcdef".AsSpan());
}
