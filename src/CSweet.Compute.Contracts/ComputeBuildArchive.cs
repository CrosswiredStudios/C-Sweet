using System.Security.Cryptography;
using System.Text;

namespace CSweet.Compute.Contracts;

/// <summary>One immutable static-build input, staged in the submitting installation's operating state.</summary>
public sealed record ComputeBuildArchive(string SourceCommitSha, string BuildSha256, int Length,
    IReadOnlyList<string> PartSha256)
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int PartBytes = 64 * 1024;
    public const string SchemaId = "compute.build-input-part.v1";
    public ComputeBuildArchive Validate()
    {
        if (SourceCommitSha is not { Length: 40 or 64 } || SourceCommitSha.Any(c => !"0123456789abcdef".Contains(c)) ||
            !ComputeEvidenceArchive.Hash(BuildSha256) || Length is < 1 or > MaximumBytes || PartSha256 is null ||
            PartSha256.Count != (Length + PartBytes - 1) / PartBytes || PartSha256.Any(x => !ComputeEvidenceArchive.Hash(x)))
            throw new ArgumentException("An exact source/build and bounded hashed input parts are required.");
        return this with { PartSha256 = PartSha256.ToArray() };
    }
    public string PartKey(int offset) => "compute-build:" + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(SourceCommitSha + ":" + BuildSha256))) + ":" + offset;
    public string GuestRoot => "/var/lib/csweet-compute/work/browser-" + BuildSha256;
}

public sealed record ComputeBuildInputPart(string SourceCommitSha, string BuildSha256, int Length, int Offset, byte[] Content);
public sealed record ComputeBuildInputPage(Guid OperationId, Guid DispatchId, int Offset, int Length, string BuildSha256, byte[] Content);
public sealed record ComputeBuildArchiveReceipt(string SourceCommitSha, string BuildSha256, int Length);
