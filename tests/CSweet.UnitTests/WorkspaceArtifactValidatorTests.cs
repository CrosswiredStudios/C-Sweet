using System.IO.Compression;
using System.Text;
using CSweet.TrustedServices;

namespace CSweet.UnitTests;

public sealed class WorkspaceArtifactValidatorTests
{
    [Fact]
    public async Task RoundTripProducesStableContentManifestWithoutGitMetadata()
    {
        var source = NewTemporaryDirectory();
        var extracted = NewTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "src"));
            await File.WriteAllTextAsync(Path.Combine(source, "src", "app.cs"), "class App {}\n");
            await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "hello\n");
            var validator = new WorkspaceArtifactValidator();
            await using var archive = new MemoryStream();
            var created = await validator.CreateZipAsync(source, archive);
            archive.Position = 0;

            var restored = await validator.ExtractZipAsync(archive, extracted);

            Assert.Equal(created, restored);
            Assert.Equal(2, restored.FileCount);
            Assert.False(Directory.Exists(Path.Combine(extracted, ".git")));
        }
        finally
        {
            DeleteTemporaryDirectory(source);
            DeleteTemporaryDirectory(extracted);
        }
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("src/../../escape.txt")]
    [InlineData(".git/config")]
    [InlineData("src/.GIT/index")]
    [InlineData("CON.txt")]
    [InlineData("src/file.txt:stream")]
    public async Task UnsafeArchivePathsAreRejected(string path)
    {
        await using var archive = Zip((path, "blocked"));
        var destination = NewTemporaryDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new WorkspaceArtifactValidator().ExtractZipAsync(archive, destination));
        }
        finally
        {
            DeleteTemporaryDirectory(destination);
        }
    }

    [Fact]
    public async Task SymbolicLinkEntryIsRejected()
    {
        await using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
            await writer.WriteAsync("target");
        }
        archive.Position = 0;
        var destination = NewTemporaryDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new WorkspaceArtifactValidator().ExtractZipAsync(archive, destination));
        }
        finally
        {
            DeleteTemporaryDirectory(destination);
        }
    }

    [Fact]
    public async Task DeclaredWorkspaceLimitsAreEnforced()
    {
        await using var archive = Zip(("one.txt", "1"), ("two.txt", "2"));
        var destination = NewTemporaryDirectory();
        try
        {
            var validator = new WorkspaceArtifactValidator(new WorkspaceArtifactLimits
            {
                MaximumFiles = 1,
                MaximumFileBytes = 10,
                MaximumTotalBytes = 10,
                MaximumPathLength = 100
            });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                validator.ExtractZipAsync(archive, destination));
        }
        finally
        {
            DeleteTemporaryDirectory(destination);
        }
    }

    private static MemoryStream Zip(params (string Path, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = zip.CreateEntry(item.Path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(item.Content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"csweet-artifact-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Path.GetFileName(path).StartsWith("csweet-artifact-test-", StringComparison.Ordinal) &&
            Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
