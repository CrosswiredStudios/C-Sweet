using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Office.Contracts.Workloads;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
namespace CSweet.UnitTests;

public sealed class WebPreviewArtifactTests
{
    private sealed class Store : IAgentArtifactStore
    {
        public byte[] Bytes { get; set; } = [];
        public int Reads { get; private set; }
        public Func<Task>? OnRead { get; set; }
        public Task<bool> ExistsAsync(string digest, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public async Task<Stream> OpenReadAsync(string digest, CancellationToken cancellationToken = default)
        { Reads++; if (OnRead is not null) await OnRead(); return new MemoryStream(Bytes); }
        public Task<AgentArtifactReference> ImportAsync(Stream content, ArtifactImportDescriptor descriptor, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Product ZIPs must not be imported as executable agent bundles.");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public CSweetDbContext Db { get; } = new(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public Store Store { get; } = new();
        public DeliveryBuildRecord Build { get; } = new()
        {
            Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), WorkstreamId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(), Status = "Succeeded", SourceRevision = new string('a',40)
        };
        public WebPreviewArtifactService Service => new(Db, Store);
        public async Task SeedAsync(params string[] extraPaths)
        {
            var files = new[] { "index.html", "game.js" }.Concat(extraPaths).ToArray();
            using var archive = new MemoryStream();
            var outputs = new List<BuildOutputManifestEntry>();
            using (var tar = new TarWriter(archive, leaveOpen: true))
            {
                foreach (var name in files.Reverse())
                {
                    var bytes = Encoding.UTF8.GetBytes(name == "index.html" ? "<html>Game preview</html>" : "asset:" + name);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/output/" + name) { DataStream = new MemoryStream(bytes) });
                    outputs.Add(new(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
                        name == "index.html" ? "text/html" : "application/javascript", "web-file"));
                }
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "payload/provenance.txt")
                    { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("not-public")) });
            }
            Store.Bytes = archive.ToArray();
            Build.OutputsJson = JsonSerializer.Serialize(outputs);
            Db.DeliveryBuilds.Add(Build);
            Db.SourceControlRepositories.Add(new() { Id = Build.RepositoryId, OrganizationId = Build.OrganizationId, Name = "Game" });
            Db.ExecutionWorkloadAssignments.Add(new()
            {
                Id = Guid.NewGuid(), DeliveryBuildId = Build.Id, BusinessId = Build.OrganizationId.ToString("D"),
                WorkloadKind = ExecutionWorkloadKind.ToolchainBuild, Status = ExecutionAssignmentStatus.Completed,
                ResultArtifactDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Store.Bytes))
            });
            await Db.SaveChangesAsync();
        }
        public Task<PreparedWebPreviewArtifact> PrepareAsync() => Service.PrepareStaticAsync(Build.OrganizationId,
            Build.WorkstreamId, Build.RepositoryId, Build.Id, Build.SourceRevision, default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact] public async Task Produces_deterministic_data_only_zip_bound_to_the_exact_build()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        using var first = await f.PrepareAsync(); using var second = await f.PrepareAsync();
        Assert.Equal(first.Digest, second.Digest); Assert.Equal(first.Length, second.Length);
        Assert.Equal(f.Build.Id, first.BuildId); Assert.Equal(f.Build.RepositoryId, first.RepositoryId);
        Assert.Equal(f.Build.SourceRevision, first.SourceRevision);
        Assert.Equal(first.Digest, "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(first.Content)));
        first.Content.Position = 0;
        using var zip = new ZipArchive(first.Content, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(new[] { "site/game.js", "site/index.html" }, zip.Entries.Select(x => x.FullName));
        Assert.All(zip.Entries, x => Assert.Equal(1980, x.LastWriteTime.Year));
        using var html = new StreamReader(zip.GetEntry("site/index.html")!.Open());
        Assert.Equal("<html>Game preview</html>", await html.ReadToEndAsync());
    }

    [Fact] public async Task Rejects_cross_scope_and_wrong_revision_before_reading_artifacts()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.PrepareStaticAsync(Guid.NewGuid(),
            f.Build.WorkstreamId, f.Build.RepositoryId, f.Build.Id, f.Build.SourceRevision, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Service.PrepareStaticAsync(f.Build.OrganizationId,
            f.Build.WorkstreamId, f.Build.RepositoryId, f.Build.Id, new string('b',40), default));
        f.Build.Status = "Failed"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => f.PrepareAsync());
        Assert.Equal(0, f.Store.Reads);
    }

    [Fact] public async Task Verifies_the_entire_archive_including_non_public_provenance_and_trailing_bytes()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Store.Bytes = [..f.Store.Bytes, 1, 2, 3];
        await Assert.ThrowsAsync<InvalidDataException>(() => f.PrepareAsync());
    }

    [Fact] public async Task Rejects_build_changes_during_preparation_and_archived_repositories()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        f.Store.OnRead = async () => { f.Build.Revision++; await f.Db.SaveChangesAsync(); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.PrepareAsync());
        f.Store.OnRead = null;
        (await f.Db.SourceControlRepositories.SingleAsync()).ArchivedAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.PrepareAsync());
    }

    [Theory]
    [InlineData("Game.js")]
    [InlineData("unsafe.")]
    public async Task Rejects_paths_that_would_collide_or_fail_in_the_product_guest(string path)
    {
        await using var f = new Fixture(); await f.SeedAsync(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.PrepareAsync());
    }
}
