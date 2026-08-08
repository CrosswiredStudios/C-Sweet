using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CSweet.AgentRuntime.Abstractions;
using CSweet.AgentRuntime.Artifacts;
using CSweet.AgentRuntime.Core;

namespace CSweet.UnitTests;

public sealed class AgentArtifactMediaStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "csweet-artifact-media-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureReadOnlyMediaAsync_CreatesVerifiedContentAddressedIso()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var store = ArtifactStore();
        await store.ImportAsync(new MemoryStream(bundle), Descriptor(digest));
        var mediaRoot = Path.Combine(_root, "media");
        var media = new FileSystemAgentArtifactMediaStore(
            new ArtifactMediaOptions { RootPath = mediaRoot }, store);

        await media.EnsureReadOnlyMediaAsync(digest);

        var path = Path.Combine(mediaRoot, $"{digest[7..]}.iso");
        Assert.True(File.Exists(path));
        Assert.True(await SingleFileIso9660.VerifyArtifactDigestAsync(path, digest));
    }

    [Fact]
    public async Task VerifyArtifactDigestAsync_RejectsTamperedMedia()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var path = Path.Combine(_root, "tampered.iso");
        Directory.CreateDirectory(_root);
        await using (var input = new MemoryStream(bundle))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            await SingleFileIso9660.WriteAsync(input, input.Length, output);
        await using (var tamper = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            tamper.Position = 21L * SingleFileIso9660.SectorSize;
            tamper.WriteByte((byte)(bundle[0] ^ 0xff));
        }

        Assert.False(await SingleFileIso9660.VerifyArtifactDigestAsync(path, digest));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private FileSystemAgentArtifactStore ArtifactStore() => new(
        new ArtifactStoreOptions { RootPath = Path.Combine(_root, "store") },
        new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32])));

    private static ArtifactImportDescriptor Descriptor(string digest) =>
        new(digest, 1024 * 1024, "1.0", "linux", "x64", "{}");

    private static byte[] Bundle()
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            Add(writer, "artifact.json",
                "{\"formatVersion\":\"1.0\",\"operatingSystem\":\"linux\",\"architecture\":\"x64\",\"entrypoint\":[\"agent\"]}");
            Add(writer, "payload/agent", "agent", executable: true);
        }
        return output.ToArray();
    }

    private static void Add(TarWriter writer, string path, string content, bool executable = false)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Uid = 0,
            Gid = 0,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                (executable ? UnixFileMode.UserExecute : 0)
        };
        writer.WriteEntry(entry);
    }

    private static string Digest(byte[] content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
}
