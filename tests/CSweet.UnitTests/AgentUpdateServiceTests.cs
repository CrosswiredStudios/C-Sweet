using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class AgentUpdateServiceTests
{
    [Theory]
    [InlineData("1.3.0", true)]
    [InlineData("2.0.0-beta.1", true)]
    [InlineData("1.2.3+new-build", false)]
    [InlineData("1.2.2", false)]
    public async Task CheckAsync_UsesManifestSemanticVersion(string repositoryVersion, bool expected)
    {
        await using var dbContext = CreateDbContext();
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = "https://github.com/example/research-agent",
            RepositoryOwner = "example",
            RepositoryName = "research-agent",
            DefaultBranch = "main"
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = source.Id,
            AgentId = "com.example.research-agent",
            AgentName = "Research Agent",
            Version = "1.2.3",
            CommitSha = new string('1', 40),
            ManifestDigest = new string('a', 64),
            ManifestJson = "{}",
            PublisherId = "com.example",
            PublisherName = "Example",
            RuntimeType = "dotnet-project",
            PackageSource = source
        };
        dbContext.AgentPackageSources.Add(source);
        dbContext.AgentPackageVersions.Add(package);
        dbContext.AgentInstallations.Add(new AgentInstallation
        {
            Id = Guid.NewGuid(),
            PackageVersionId = package.Id,
            BusinessId = "default",
            PackageVersion = package
        });
        await dbContext.SaveChangesAsync();
        var preview = CreatePreview(repositoryVersion);
        var service = new AgentUpdateService(
            dbContext,
            new StubPreviewService(preview),
            NullLogger<AgentUpdateService>.Instance);

        var result = Assert.Single(await service.CheckAsync());

        Assert.Equal(expected, result.UpdateAvailable);
        Assert.Equal(expected ? preview.ImportId : null, result.AvailablePackageVersionId);
    }

    [Theory]
    [InlineData("present")]
    [InlineData("present", true)]
    [InlineData("missing")]
    [InlineData("error")]
    [InlineData("invalid-utf8")]
    public async Task CheckDefinitionsAsync_ReportsUpdatesWithCommitPinnedReleaseNotes(string notesMode, bool targeted = false)
    {
        await using var dbContext = CreateDbContext();
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/example/research-agent",
            RepositoryOwner = "example", RepositoryName = "research-agent", DefaultBranch = "main"
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = source.Id, PackageSource = source,
            AgentId = "com.example.research-agent", AgentName = "Research Agent", Version = "1.2.3",
            CommitSha = new string('1', 40), ManifestDigest = new string('a', 64), ManifestJson = "{}",
            PublisherId = "com.example", PublisherName = "Example", RuntimeType = "dotnet-project"
        };
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(), PackageSourceId = source.Id, AgentId = package.AgentId,
            PackageVersionId = package.Id, PackageVersion = package, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.AddRange(source, package, definition);
        if (targeted)
        {
        var otherSource = new AgentPackageSource
        {
            Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/example/unrelated-agent",
            RepositoryOwner = "example", RepositoryName = "unrelated-agent", DefaultBranch = "main"
        };
        var otherPackage = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = otherSource.Id, PackageSource = otherSource,
            AgentId = "com.example.unrelated-agent", AgentName = "Research Agent", Version = "1.2.3",
            CommitSha = new string('1', 40), ManifestDigest = new string('a', 64), ManifestJson = "{}",
            PublisherId = "com.example", PublisherName = "Example", RuntimeType = "dotnet-project"
        };
        var otherDefinition = new AgentDefinition
        {
            Id = Guid.NewGuid(), PackageSourceId = otherSource.Id, AgentId = otherPackage.AgentId,
            PackageVersionId = otherPackage.Id, PackageVersion = otherPackage, CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.AddRange(otherSource, otherPackage, otherDefinition);
        }
        await dbContext.SaveChangesAsync();
        var preview = CreatePreview("1.3.0");
        var repository = new NotesRepository(notesMode);
        var previewService = new StubPreviewService(preview);
        var service = new AgentUpdateService(
            dbContext, previewService, NullLogger<AgentUpdateService>.Instance, repository);

        var result = Assert.Single(await service.CheckDefinitionsAsync(definitionId: targeted ? definition.Id : null));
        Assert.Equal(1, previewService.Calls);

        Assert.Equal(definition.Id, result.DefinitionId);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(preview.ImportId, result.AvailablePackageVersionId);
        Assert.Equal(preview.CommitSha, repository.Commit);
        Assert.Equal("releases/1.3.0.md", repository.Path);
        Assert.Equal(64 * 1024, repository.MaximumBytes);
        Assert.Equal("releases/1.3.0.md", result.ReleaseNotesPath);
        Assert.Equal(notesMode == "present" ? "# 1.3.0\n- Improved scheduling." : null, result.ReleaseNotes);
        Assert.Equal(notesMode is "error" or "invalid-utf8", result.ReleaseNotesError is not null);
        Assert.Null(result.Error);
    }

    private static AgentImportPreviewResponse CreatePreview(string version) => new(
        Guid.NewGuid(),
        "https://github.com/example/research-agent",
        new string('2', 40),
        new string('b', 64),
        "com.example.research-agent",
        "Research Agent",
        version,
        "com.example",
        "Example",
        "dotnet-project",
        "src/ResearchAgent/ResearchAgent.csproj",
        "net10.0",
        "Scheduled",
        [], [], [], [], [], [],
        "Previewed");

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CSweetDbContext(options);
    }

    private sealed class NotesRepository(string mode) : IGitHubAgentRepositoryClient
    {
        public string? Commit { get; private set; }
        public string? Path { get; private set; }
        public int MaximumBytes { get; private set; }
        public Task<string> GetDefaultBranchAsync(string owner, string repository, CancellationToken token) => throw new NotSupportedException();
        public Task<string> ResolveCommitShaAsync(string owner, string repository, string reference, CancellationToken token) => throw new NotSupportedException();
        public Task<byte[]> GetRootManifestAsync(string owner, string repository, string commit, CancellationToken token) => throw new NotSupportedException();
        public Task<byte[]?> GetRepositoryFileAsync(string owner, string repository, string commit, string path, int maximumBytes, CancellationToken token)
        {
            Commit = commit;
            Path = path;
            MaximumBytes = maximumBytes;
            if (mode == "error") throw new HttpRequestException("Unavailable");
            return Task.FromResult<byte[]?>(mode switch
            {
                "present" => System.Text.Encoding.UTF8.GetBytes("# 1.3.0\n- Improved scheduling."),
                "invalid-utf8" => [0xff, 0xfe],
                _ => null
            });
        }
    }

    private sealed class StubPreviewService(AgentImportPreviewResponse response) : IAgentImportPreviewService
    {
        public int Calls { get; private set; }
        public Task<AgentImportPreviewResponse> PreviewAsync(
            PreviewAgentImportRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(response);
        }
    }
}
