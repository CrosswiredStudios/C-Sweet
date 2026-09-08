namespace CSweet.Contracts.GenAi;

/// <summary>Limits for retained attachment references, separate from inference byte limits.</summary>
public static class MediaAttachmentPolicy
{
    public const int MaximumCount = 8;
    public const long AbsoluteMaximumFileBytes = 256L * 1024 * 1024 * 1024;
    public const long SmallFileBytes = 25L * 1024 * 1024;
    public const long SmallTotalBytes = 50L * 1024 * 1024;
    public const string Accept = "image/png,image/jpeg,image/webp,application/pdf,text/plain,text/markdown,video/mp4,video/webm,text/vtt,application/x-subrip,.md,.markdown,.srt,.vtt";
    public static bool IsVideo(string type) => type is "video/mp4" or "video/webm";
    public static bool IsSupported(string type) => IsVideo(type) || type is "image/png" or "image/jpeg" or
        "image/webp" or "application/pdf" or "text/plain" or "text/markdown" or "text/vtt" or "application/x-subrip";
    public static string ContentType(string name, string browserType) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".srt" => "application/x-subrip", ".vtt" => "text/vtt",
        ".md" or ".markdown" => "text/markdown", ".txt" => "text/plain", ".pdf" => "application/pdf",
        _ => browserType.Split(';', 2)[0].Trim().ToLowerInvariant()
    };
    public static void Validate(IEnumerable<(string ContentType, long SizeBytes)> files, long maximumFileBytes)
    {
        var values = files.ToArray();
        if (values.Length > MaximumCount) throw new InvalidOperationException("A message can contain at most 8 attachments.");
        var limit = Math.Clamp(maximumFileBytes, 1, AbsoluteMaximumFileBytes);
        foreach (var file in values)
        {
            if (!IsSupported(file.ContentType)) throw new InvalidOperationException("This file type is not supported for attachments.");
            if (file.SizeBytes <= 0 || file.SizeBytes > (IsVideo(file.ContentType) ? limit : Math.Min(limit, SmallFileBytes)))
                throw new InvalidOperationException(IsVideo(file.ContentType)
                    ? "This video exceeds the deployment's file size limit."
                    : "Images, documents and captions must fit the deployment limit and be 25 MB or smaller.");
        }
        if (values.Where(x => !IsVideo(x.ContentType)).Sum(x => x.SizeBytes) > SmallTotalBytes)
            throw new InvalidOperationException("Images, documents and captions must total 50 MB or less per message.");
    }
}

public sealed record MediaUploadPolicyResponse(long MaximumFileSizeBytes);
