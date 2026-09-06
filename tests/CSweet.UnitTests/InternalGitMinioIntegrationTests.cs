using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CSweet.Contracts.SourceControl;
using CSweet.TrustedServices;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class MinioIntegrationFactAttribute : FactAttribute
{
    public MinioIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_TEST_MINIO_ENDPOINT")))
            Skip = "Run scripts/Test-InternalGitMinio.ps1 to supply a disposable MinIO instance.";
    }
}

public sealed class InternalGitMinioIntegrationTests
{
    [MinioIntegrationFact]
    public async Task MultipartBackupRecoversHistoricalLfsWithoutSourceAndRejectsCorruption()
    {
        var endpoint = Environment.GetEnvironmentVariable("CSWEET_TEST_MINIO_ENDPOINT")!;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback) throw new InvalidOperationException("This destructive integration fixture requires a disposable loopback endpoint.");
        var access = Environment.GetEnvironmentVariable("CSWEET_TEST_MINIO_ACCESS")!;
        var secret = Environment.GetEnvironmentVariable("CSWEET_TEST_MINIO_SECRET")!;
        var bucket = "csweet-test-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), bucket); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".csweet-git-store"), bucket);
        using var client = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            InternalGitObjectStorageOptions Objects(string prefix) => new() { Provider = "s3", ServiceUrl = endpoint, BucketName = bucket, KeyPrefix = prefix,
                Region = "us-east-1", ForcePathStyle = true, AccessKeyId = access, SecretAccessKey = secret };
            var options = new InternalGitStorageOptions { RepositoryRoot = root, ExpectedStoreId = bucket, TemporaryRoot = Path.Combine(root, "temp"), Lfs = Objects("lfs"), Backup = Objects("backups") };
            var store = new InternalGitRepositoryStore(Options.Create(options)); var artifacts = new WorkspaceArtifactValidator();
            var business = Guid.NewGuid(); var repository = Guid.NewGuid(); var workspace = Guid.NewGuid(); var backupId = Guid.NewGuid();
            await store.ExecuteAsync(new(business, repository, "create", "main"));
            var initial = await store.PrepareAsync(new(business, repository, workspace, "main", "work", null, "prepare"), artifacts);
            var firstAsset = new byte[18 * 1024 * 1024]; Random.Shared.NextBytes(firstAsset);
            var secondAsset = new byte[] { 0, 1, 2, 255 };
            async Task<string> Publish(string sha, byte[] asset, string key)
            {
                var input = Path.Combine(root, key); Directory.CreateDirectory(input);
                await File.WriteAllTextAsync(Path.Combine(input, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n");
                await File.WriteAllBytesAsync(Path.Combine(input, "asset.bin"), asset);
                using var archive = new MemoryStream(); var manifest = await artifacts.CreateZipAsync(input, archive);
                return (await store.ApplySnapshotAsync(new(business, repository, workspace, "publish", sha, "work", "main", key,
                    archive.ToArray(), manifest.Sha256, manifest.FileCount, manifest.TotalBytes, key), artifacts)).CommitSha!;
            }
            var first = await Publish(initial.BaseCommitSha, firstAsset, "first"); var second = await Publish(first, secondAsset, "second");
            await store.ExecuteAsync(new(business, repository, "update-ref", Ref: "refs/tags/v1", ExpectedSha: new string('0', 40), TargetSha: first));
            var request = new InternalGitBackupRequest(business, repository, backupId);
            var backup = await store.CreateBackupAsync(request); Assert.Equal(2, backup.LfsObjectCount);
            Assert.Equal(backup, await store.CreateBackupAsync(request)); Assert.Single(await store.ListBackupsAsync(business)); Assert.Empty(await store.ListBackupsAsync(Guid.NewGuid()));
            var archiveKey = $"backups/{business:N}/{repository:N}/{backupId:N}/archive.zip";
            var metadata = await client.GetObjectMetadataAsync(bucket, archiveKey);
            Assert.True(metadata.ContentLength > 16 * 1024 * 1024); Assert.Contains("-", metadata.ETag); // Multipart object ETag.
            using (var anonymous = new HttpClient())
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await anonymous.GetAsync(endpoint.TrimEnd('/') + "/" + bucket + "/" + archiveKey)).StatusCode);
            await store.ExecuteAsync(new(business, repository, "delete"));
            var sourceObjects = await client.ListObjectsV2Async(new() { BucketName = bucket, Prefix = $"lfs/{business:N}/{repository:N}/" });
            Assert.Equal(2, sourceObjects.S3Objects.Count);
            foreach (var item in sourceObjects.S3Objects) await client.DeleteObjectAsync(bucket, item.Key);
            var restoredId = Guid.NewGuid(); var restore = new InternalGitBackupRestoreRequest(business, repository, backupId, restoredId);
            await store.RestoreBackupAsync(restore); await store.RestoreBackupAsync(restore);
            foreach (var revision in new[] { (first, firstAsset), (second, secondAsset) })
            {
                var snapshot = await store.PrepareAsync(new(business, restoredId, Guid.NewGuid(), "main", "work", revision.Item1, "recovered"), artifacts);
                var output = Path.Combine(root, Guid.NewGuid().ToString("N")); using var zip = new MemoryStream(snapshot.Archive);
                await artifacts.ExtractZipAsync(zip, output); Assert.Equal(revision.Item2, await File.ReadAllBytesAsync(Path.Combine(output, "asset.bin")));
            }
            var refs = (await store.ExecuteAsync(new(business, restoredId, "inspect"))).Refs;
            Assert.Contains(refs, r => r.Name == "refs/tags/v1" && r.Sha == first); Assert.Contains(refs, r => r.Name == "refs/heads/work" && r.Sha == second);
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = archiveKey, ContentBody = "corrupted backup" });
            await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreBackupAsync(new(business, repository, backupId, Guid.NewGuid())));
            await store.DeleteBackupAsync(request); Assert.Empty(await store.ListBackupsAsync(business));
        }
        finally
        {
            // This bucket was generated and created by this test; never enumerate or remove other buckets.
            while (true)
            {
                var page = await client.ListObjectsV2Async(new() { BucketName = bucket });
                if (page.S3Objects is null || page.S3Objects.Count == 0) break;
                foreach (var item in page.S3Objects) await client.DeleteObjectAsync(bucket, item.Key);
            }
            await client.DeleteBucketAsync(bucket);
            if (Directory.Exists(root)) { foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(root, true); }
        }
    }
}
