namespace CSweet.Domain.Setup;

/// <summary>Host-recorded byte proof, produced while the original asset is ingested.</summary>
public sealed class MediaAssetChunk
{
    public Guid MediaAssetId { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string AssetSha256 { get; set; } = string.Empty;
}
