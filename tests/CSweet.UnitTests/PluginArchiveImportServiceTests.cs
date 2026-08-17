using System.IO.Compression;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class PluginArchiveImportServiceTests
{
    [Fact]
    public async Task PreviewSourceArchiveAsync_PersistsContentAddressedSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-archive-test", Guid.NewGuid().ToString("N"));
        try
        {
            await using var db = CreateDb();
            var service = new PluginArchiveImportService(
                db,
                new PluginManifestReader(),
                new TestAuditEventWriter(),
                Options.Create(new AgentRuntimeManagerOptions { SourceArchiveStorePath = root }));
            await using var archive = Archive(("csweet-plugin.json", Manifest()), ("src/Test/Test.csproj", "<Project />"));

            var result = await service.PreviewSourceArchiveAsync(archive, "test.zip");

            Assert.StartsWith("upload://", result.RepositoryUrl);
            Assert.Equal("csweet-plugin.json", result.ManifestFileName);
            var source = await db.AgentPackageSources.SingleAsync();
            Assert.Equal("SourceArchive", source.SourceType);
            Assert.True(File.Exists(source.SourceArchivePath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PreviewSourceArchiveAsync_RejectsTraversalBeforePersistence()
    {
        await using var db = CreateDb();
        var root = Path.Combine(Path.GetTempPath(), "csweet-archive-test", Guid.NewGuid().ToString("N"));
        var service = new PluginArchiveImportService(
            db,
            new PluginManifestReader(),
            new TestAuditEventWriter(),
            Options.Create(new AgentRuntimeManagerOptions { SourceArchiveStorePath = root }));
        await using var archive = Archive(("csweet-plugin.json", Manifest()), ("../escape.txt", "no"));

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewSourceArchiveAsync(archive, "hostile.zip"));

        Assert.Contains("traversal", exception.Message);
        Assert.Empty(await db.AgentPackageSources.ToListAsync());
    }

    private static MemoryStream Archive(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(file.Content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static string Manifest() => """
        {
          "manifestVersion":"2.0", "kind":"agent", "id":"com.example.test", "name":"Test", "version":"1.0.0",
          "publisher":{"id":"com.example","name":"Example"},
          "runtime":{"type":"dotnet-project","projectPath":"src/Test/Test.csproj","targetFramework":"net10.0","defaultActivationMode":"OnDemand","maximumConcurrentJobs":1},
          "protocol":{"minimumVersion":"2.0","maximumVersion":"2.x"},
          "catalog":{"role":{"key":"test-agent","name":"Test Agent"},"license":{"spdxId":"MIT"}},
          "provides":[], "requires":[], "events":{"subscribes":[]},
          "configuration":[], "credentials":[], "webAccess":{"mode":"None","rules":[]}, "ui":[]
        }
        """;

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
