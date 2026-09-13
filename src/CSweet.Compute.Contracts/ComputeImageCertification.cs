using CSweet.Domain.Compute;

namespace CSweet.Compute.Contracts;

public sealed record ComputeImageCertification(string Purpose, string ProviderId, string ProviderVersion,
    ComputeTemplate Template, string SuiteVersion, HashSet<string> Controls, Dictionary<string, string> RuntimeFiles,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public sealed record SignedComputeImageCertification(string CertificateJson, string SignatureBase64);
