using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CSweet.AgentBroker;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Artifacts;

namespace CSweet.UnitTests;

public sealed class BuilderArtifactBrokerStreamHandlerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-builder-stream-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OrderedCompletedStream_IsValidatedSignedAndPublished()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var bundle = Bundle();
        var digest = Digest(bundle);
        var publisher = new CapturingPublisher();
        var store = new FileSystemAgentArtifactStore(
            new ArtifactStoreOptions { RootPath = Path.Combine(_root, "store") },
            new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32])));
        await using var handler = new BuilderArtifactBrokerStreamHandler(
            new BuilderArtifactStreamGrant(
                workloadId, installationId, "builder-artifact", 10 * 1024 * 1024,
                "1.0", "linux", "x64", "{}"),
            store,
            publisher,
            Path.Combine(_root, "staging"));

        var split = bundle.Length / 2;
        await handler.HandleAsync(new GuestBrokerStreamContext(
            workloadId, installationId, "builder-artifact", 0,
            bundle.AsMemory(0, split), false, null), CancellationToken.None);
        await handler.HandleAsync(new GuestBrokerStreamContext(
            workloadId, installationId, "builder-artifact", 1,
            bundle.AsMemory(split), true, digest), CancellationToken.None);

        var result = Assert.Single(publisher.Results);
        Assert.Equal(workloadId, result.WorkloadId);
        Assert.Equal(digest, result.Artifact.Digest);
        Assert.Equal($"artifact:{digest}", result.OpaqueLocator);
        Assert.True(await store.ExistsAsync(digest));
    }

    [Fact]
    public async Task OutOfOrderStream_IsRejected()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var store = new FileSystemAgentArtifactStore(
            new ArtifactStoreOptions { RootPath = Path.Combine(_root, "store") },
            new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32])));
        await using var handler = new BuilderArtifactBrokerStreamHandler(
            new BuilderArtifactStreamGrant(
                workloadId, installationId, "builder-artifact", 1024,
                "1.0", "linux", "x64", "{}"),
            store,
            new CapturingPublisher(),
            Path.Combine(_root, "staging"));

        await Assert.ThrowsAsync<InvalidDataException>(() => handler.HandleAsync(
            new GuestBrokerStreamContext(
                workloadId, installationId, "builder-artifact", 1,
                ReadOnlyMemory<byte>.Empty, false, null),
            CancellationToken.None));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private static byte[] Bundle()
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            Add(writer, "artifact.json", "{\"formatVersion\":\"1.0\",\"operatingSystem\":\"linux\",\"architecture\":\"x64\",\"entrypoint\":[\"/app/agent\"]}");
            Add(writer, "payload/agent", "payload");
        }
        return output.ToArray();
    }

    private static void Add(TarWriter writer, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Uid = 0,
            Gid = 0,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
        };
        writer.WriteEntry(entry);
    }

    private static string Digest(byte[] content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class CapturingPublisher : IBuilderArtifactResultPublisher
    {
        public List<BuilderArtifactResult> Results { get; } = [];
        public Task PublishAsync(BuilderArtifactResult result, CancellationToken cancellationToken = default)
        {
            Results.Add(result);
            return Task.CompletedTask;
        }
    }
}
