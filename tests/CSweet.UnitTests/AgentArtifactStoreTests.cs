using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.ExecutionArtifacts;

namespace CSweet.UnitTests;

public sealed class AgentArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-artifact-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportAsync_StoresValidatedBundleByDigestAndSignsIt()
    {
        var bundle = Bundle();
        var digest = Digest(bundle);
        var signer = new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32]));
        var store = Store(signer);

        var artifact = await store.ImportAsync(
            new MemoryStream(bundle),
            Descriptor(digest));

        Assert.Equal(digest, artifact.Digest);
        Assert.True(signer.Verify(digest, "{}", artifact.Signature));
        Assert.True(await store.ExistsAsync(digest));
        await using var stored = await store.OpenReadAsync(digest);
        Assert.Equal(bundle, await ReadAllAsync(stored));
    }

    [Fact]
    public async Task ImportAsync_RejectsTraversalEntry()
    {
        var bundle = Bundle(("../escape", "bad"));
        var store = Store(new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32])));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ImportAsync(new MemoryStream(bundle), Descriptor(Digest(bundle))));
    }

    [Fact]
    public async Task ImportAsync_RejectsDigestMismatch()
    {
        var bundle = Bundle();
        var store = Store(new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32])));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ImportAsync(
                new MemoryStream(bundle),
                Descriptor("sha256:" + new string('0', 64))));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private FileSystemAgentArtifactStore Store(IAgentArtifactSigner signer) => new(
        new ArtifactStoreOptions { RootPath = _root },
        signer);

    private static ArtifactImportDescriptor Descriptor(string digest) => new(
        digest,
        10 * 1024 * 1024,
        "1.0",
        "linux",
        "x64",
        "{}");

    private static byte[] Bundle(params (string Name, string Content)[] extraEntries)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            Add(writer, "artifact.json", "{\"formatVersion\":\"1.0\",\"operatingSystem\":\"linux\",\"architecture\":\"x64\",\"entrypoint\":[\"agent\"]}");
            Add(writer, "payload/agent", "payload", executable: true);
            foreach (var entry in extraEntries) Add(writer, entry.Name, entry.Content);
        }
        return output.ToArray();
    }

    private static void Add(TarWriter writer, string name, string content, bool executable = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Uid = 0,
            Gid = 0,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                (executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0)
        };
        writer.WriteEntry(entry);
    }

    private static string Digest(byte[] content) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static async Task<byte[]> ReadAllAsync(Stream input)
    {
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }
}
