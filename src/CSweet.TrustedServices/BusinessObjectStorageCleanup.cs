using Amazon.S3;

namespace CSweet.TrustedServices;

internal static class BusinessObjectStorageCleanup
{
    // Exact generated prefix, including the final slash. No list/delete ever targets a bucket root.
    public static string Prefix(InternalGitObjectStorageOptions settings, Guid business)
    {
        settings.Validate();
        var prefix = settings.KeyPrefix.Trim('/');
        if (business == Guid.Empty || string.IsNullOrEmpty(prefix) ||
            prefix.Split('/').Any(x => x is "" or "." or ".." || x.Contains('\\') || x.Contains(':')))
            throw new IOException("Invalid business object storage prefix.");
        return $"{prefix}/{business:N}/";
    }

    public static async Task DeleteAsync(IAmazonS3 client, string bucket, string prefix, CancellationToken token)
    {
        // Delete historical versions and delete markers too; deleting only current keys does not reclaim versioned storage.
        while (true)
        {
            var versions = await client.ListVersionsAsync(new Amazon.S3.Model.ListVersionsRequest { BucketName = bucket, Prefix = prefix, MaxKeys = 1000 }, token);
            var entries = versions.Versions ?? [];
            if (entries.Count == 0) break;
            foreach (var entry in entries)
            {
                if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Object version escaped its business prefix.");
                await client.DeleteObjectAsync(new Amazon.S3.Model.DeleteObjectRequest
                { BucketName = bucket, Key = entry.Key, VersionId = entry.VersionId }, token);
            }
        }
        // Re-list from the beginning after each page: crashes/retries and deleted continuation keys are safe.
        while (true)
        {
            var page = await client.ListObjectsV2Async(new() { BucketName = bucket, Prefix = prefix, MaxKeys = 1000 }, token);
            var objects = page.S3Objects ?? [];
            if (objects.Count == 0) return;
            foreach (var item in objects)
            {
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("Object escaped its business prefix.");
                await client.DeleteObjectAsync(bucket, item.Key, token);
            }
        }
    }
}
