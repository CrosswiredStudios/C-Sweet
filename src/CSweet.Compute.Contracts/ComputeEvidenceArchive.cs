using System.Security.Cryptography;

namespace CSweet.Compute.Contracts;

/// <summary>
/// Export one exact browser evidence archive. Paths are derived, never caller supplied. There is no product size cap:
/// the provider streams the archive to Core in <see cref="PartBytes"/> parts, fenced by the live dispatch, and Core
/// verifies the whole archive against <see cref="Sha256"/> and <see cref="Length"/> before the result is accepted.
/// </summary>
public sealed record ComputeEvidenceArchive(string SourceCommitSha, string BuildSha256, string Sha256,
    int Length, string? ScriptSha256 = null)
{
    /// <summary>Largest archive an older provider or Core may still carry inline in the signed result.</summary>
    public const int LegacyInlineMaximumBytes = 4 * 1024 * 1024;
    /// <summary>Staged upload part size. A multiple of <see cref="PageBytes"/>.</summary>
    public const int PartBytes = 1024 * 1024;
    public const int PageBytes = 64 * 1024;
    [System.Text.Json.Serialization.JsonIgnore]
    public int PartCount => (int)((Length + (long)PartBytes - 1) / PartBytes);
    public int PartLength(int offset) => offset < 0 || offset >= Length || offset % PartBytes != 0
        ? throw new ArgumentOutOfRangeException(nameof(offset)) : Math.Min(PartBytes, Length - offset);
    public static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    public ComputeEvidenceArchive Validate()
    {
        if (SourceCommitSha is not { Length: 40 or 64 } || SourceCommitSha.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            !Hash(BuildSha256) || !Hash(Sha256) || ScriptSha256 is not null && !Hash(ScriptSha256) || Length < 1)
            throw new ArgumentException("An exact source, build, evidence digest and bounded length are required.");
        return this;
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string GuestPath => "/var/lib/csweet-compute/work/browser-" + BuildSha256 +
        (ScriptSha256 is null ? "" : "/checks-" + ScriptSha256) + "/evidence/evidence.zip";
    /// <summary>An inline (legacy) artifact: identity plus the complete content and its hash.</summary>
    public void Verify(ComputeEvidenceArtifact artifact)
    {
        VerifyIdentity(artifact);
        if (artifact.Content is not { } content || content.Length != Length || Length > LegacyInlineMaximumBytes ||
            Convert.ToHexStringLower(SHA256.HashData(content)) != Sha256)
            throw new InvalidDataException("Evidence archive differs from the pinned transfer request.");
    }

    /// <summary>The artifact names exactly this request. Staged content is verified separately, part by part.</summary>
    public void VerifyIdentity(ComputeEvidenceArtifact artifact)
    {
        Validate();
        if (artifact.SourceCommitSha != SourceCommitSha || artifact.BuildSha256 != BuildSha256 ||
            artifact.ScriptSha256 != ScriptSha256 || artifact.Sha256 != Sha256 || artifact.Length != Length)
            throw new InvalidDataException("Evidence archive differs from the pinned transfer request.");
    }
}

/// <summary>
/// The exported archive's identity. <see cref="Content"/> is set only by legacy providers that carry the archive inline;
/// current providers stage parts first (<see cref="ComputeEvidencePartUpload"/>) and send no content. Status reads omit it.
/// </summary>
public sealed record ComputeEvidenceArtifact(string SourceCommitSha, string BuildSha256, string Sha256,
    int Length, string? ScriptSha256 = null, byte[]? Content = null);

/// <summary>One staged evidence part. The signed request binds operation, dispatch, offset and the part's SHA-256.</summary>
public sealed record ComputeEvidencePartUpload(SignedComputeProviderWorkRequest Request, byte[] Content);
public sealed record ComputeEvidencePartReceipt(Guid OperationId, Guid DispatchId, int Offset, string Sha256);
public sealed record ComputeEvidencePage(Guid ArtifactId, string Sha256, int Length, int Offset, byte[] Content);
