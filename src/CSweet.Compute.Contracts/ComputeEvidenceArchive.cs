using System.Security.Cryptography;

namespace CSweet.Compute.Contracts;

/// <summary>Export one exact browser evidence archive. Paths are derived, never caller supplied.</summary>
public sealed record ComputeEvidenceArchive(string SourceCommitSha, string BuildSha256, string Sha256,
    int Length, string? ScriptSha256 = null)
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int PageBytes = 64 * 1024;
    public static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    public ComputeEvidenceArchive Validate()
    {
        if (SourceCommitSha is not { Length: 40 or 64 } || SourceCommitSha.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            !Hash(BuildSha256) || !Hash(Sha256) || ScriptSha256 is not null && !Hash(ScriptSha256) || Length is < 1 or > MaximumBytes)
            throw new ArgumentException("An exact source, build, evidence digest and bounded length are required.");
        return this;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string GuestPath => "/var/lib/csweet-compute/work/browser-" + BuildSha256 +
        (ScriptSha256 is null ? "" : "/checks-" + ScriptSha256) + "/evidence/evidence.zip";
    public void Verify(ComputeEvidenceArtifact artifact)
    {
        Validate();
        if (artifact.SourceCommitSha != SourceCommitSha || artifact.BuildSha256 != BuildSha256 ||
            artifact.ScriptSha256 != ScriptSha256 || artifact.Sha256 != Sha256 || artifact.Length != Length ||
            artifact.Content is not { } content || content.Length != Length ||
            Convert.ToHexStringLower(SHA256.HashData(content)) != Sha256)
            throw new InvalidDataException("Evidence archive differs from the pinned transfer request.");
    }
}

/// <summary>Content is retained by the provider outbox and platform operation; normal status reads omit it.</summary>
public sealed record ComputeEvidenceArtifact(string SourceCommitSha, string BuildSha256, string Sha256,
    int Length, string? ScriptSha256 = null, byte[]? Content = null);
public sealed record ComputeEvidencePage(Guid ArtifactId, string Sha256, int Length, int Offset, byte[] Content);
