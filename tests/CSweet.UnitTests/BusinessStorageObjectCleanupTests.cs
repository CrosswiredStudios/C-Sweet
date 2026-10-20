using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using CSweet.TrustedServices;

namespace CSweet.UnitTests;

public sealed class BusinessStorageObjectCleanupTests
{
    [Fact]
    public async Task DeletesEveryVersionAndMarkerOnlyWithinTheExactBusinessPrefix()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3>();
        var recording = (RecordingS3)(object)client;
        var prefix = "objects/" + Guid.NewGuid().ToString("N") + "/";
        recording.Versions.AddRange([
            new S3ObjectVersion { Key = prefix + "file", VersionId = "old" },
            new S3ObjectVersion { Key = prefix + "file", VersionId = "current" },
            new S3ObjectVersion { Key = prefix + "file", VersionId = "marker", IsDeleteMarker = true },
            new S3ObjectVersion { Key = prefix.TrimEnd('/') + "-other/file", VersionId = "keep" }
        ]);
        await BusinessObjectStorageCleanup.DeleteAsync(client, "fixture", prefix, default);
        await BusinessObjectStorageCleanup.DeleteAsync(client, "fixture", prefix, default);
        Assert.Equal("keep", Assert.Single(recording.Versions).VersionId);
        Assert.Equal(3, recording.Deleted.Count);
        Assert.All(recording.Deleted, item => Assert.StartsWith(prefix, item.Key));
    }

    [Fact]
    public async Task RefusesAnObjectStoreResponseOutsideTheRequestedBusiness()
    {
        var client = DispatchProxy.Create<IAmazonS3, RecordingS3>();
        var recording = (RecordingS3)(object)client;
        recording.EscapePrefix = true;
        recording.Versions.Add(new S3ObjectVersion { Key = "other-business/file", VersionId = "keep" });
        await Assert.ThrowsAsync<IOException>(() => BusinessObjectStorageCleanup.DeleteAsync(client, "fixture", "objects/business/", default));
        Assert.Empty(recording.Deleted);
    }

    public class RecordingS3 : DispatchProxy
    {
        public List<S3ObjectVersion> Versions { get; } = [];
        public List<DeleteObjectRequest> Deleted { get; } = [];
        public bool EscapePrefix;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "ListVersionsAsync":
                    var request = (ListVersionsRequest)args![0]!;
                    return Task.FromResult(new ListVersionsResponse
                    { Versions = Versions.Where(x => EscapePrefix || x.Key.StartsWith(request.Prefix, StringComparison.Ordinal)).ToList() });
                case "DeleteObjectAsync":
                    var deletion = (DeleteObjectRequest)args![0]!;
                    Deleted.Add(deletion);
                    Versions.RemoveAll(x => x.Key == deletion.Key && x.VersionId == deletion.VersionId);
                    return Task.FromResult(new DeleteObjectResponse());
                case "ListObjectsV2Async": return Task.FromResult(new ListObjectsV2Response { S3Objects = [] });
                case "Dispose": return null;
                default: throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
