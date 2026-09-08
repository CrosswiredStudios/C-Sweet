using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CSweet.Infrastructure.WorkManagement;
using CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class WebPreviewBundleTests
{
    [Fact]
    public async Task ReadsActualHtmlScriptAndWasmButDoesNotExposeBuildProvenance()
    {
        var html = Encoding.UTF8.GetBytes("<canvas id='game'></canvas><script src='game.js'></script>");
        var script = Encoding.UTF8.GetBytes("document.querySelector('canvas').width=320;");
        byte[] wasm = [0, 97, 115, 109, 1, 0, 0, 0];
        using var archive = Archive(("index.html", html), ("game.js", script), ("game.wasm", wasm));
        var result = await WebPreviewBundle.ReadAsync(archive,
            [Entry("index.html", html, "text/html"), Entry("game.js", script, "text/javascript"), Entry("game.wasm", wasm, "application/wasm")]);
        Assert.Equal(3, result.Files.Count);
        Assert.Equal(wasm, result.Files["game.wasm"].Content);
        Assert.Equal(html, result.Files["index.html"].Content);
        Assert.DoesNotContain("provenance.json", result.Files.Keys);
    }

    [Theory]
    [InlineData("../index.html")]
    [InlineData("/index.html")]
    [InlineData("a//index.html")]
    [InlineData("a/./index.html")]
    [InlineData("a/%2e%2e/index.html")]
    [InlineData("C:index.html")]
    [InlineData("a\\index.html")]
    public void RejectsAmbiguousAndEscapingPaths(string path) => Assert.False(WebPreviewBundle.ValidPath(path));

    [Theory]
    [InlineData("digest")]
    [InlineData("missing")]
    [InlineData("undeclared")]
    [InlineData("duplicate")]
    [InlineData("size")]
    public async Task RejectsArchiveThatDoesNotMatchPublishedManifest(string problem)
    {
        var bytes = Encoding.UTF8.GetBytes("<html>Game</html>");
        var manifest = new[] { Entry("index.html", bytes, "text/html") };
        if (problem == "digest") manifest[0] = manifest[0] with { Sha256 = new string('0',64) };
        if (problem == "size") manifest[0] = manifest[0] with { Size = bytes.Length + 1 };
        var files = new List<(string, byte[])>();
        if (problem != "missing") files.Add(("index.html", bytes));
        if (problem == "duplicate") files.Add(("index.html", bytes));
        if (problem == "undeclared") files.Add(("secret.txt", bytes));
        using var archive = Archive(files.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => WebPreviewBundle.ReadAsync(archive, manifest));
    }

    private static BuildOutputManifestEntry Entry(string name, byte[] bytes, string contentType) =>
        new(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, contentType, "web-file");

    private static MemoryStream Archive(params (string Name, byte[] Bytes)[] files)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/provenance.json") { DataStream = new MemoryStream([123,125]) });
            foreach (var (name, bytes) in files)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/output/" + name) { DataStream = new MemoryStream(bytes) });
        }
        stream.Position = 0;
        return stream;
    }
}
