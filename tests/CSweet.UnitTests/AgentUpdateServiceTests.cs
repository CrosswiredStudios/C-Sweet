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

    private sealed class StubPreviewService(AgentImportPreviewResponse response) : IAgentImportPreviewService
    {
        public Task<AgentImportPreviewResponse> PreviewAsync(
            PreviewAgentImportRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(response);
    }
}
