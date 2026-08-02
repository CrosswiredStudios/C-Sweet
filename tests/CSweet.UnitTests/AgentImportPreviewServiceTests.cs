using System.Text;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public class AgentImportPreviewServiceTests
{
    [Theory]
    [InlineData("https://github.com/example/research-agent", "https://github.com/example/research-agent")]
    [InlineData("https://github.com/example/research-agent.git/", "https://github.com/example/research-agent")]
    public void Normalize_AcceptsRepositoryUrls(string input, string expected)
    {
        var repository = GitHubRepositoryUrlNormalizer.Normalize(input);

        Assert.Equal("example", repository.Owner);
        Assert.Equal("research-agent", repository.Name);
        Assert.Equal(expected, repository.RepositoryUrl);
    }

    [Theory]
    [InlineData("http://github.com/example/research-agent")]
    [InlineData("https://gitlab.com/example/research-agent")]
    [InlineData("https://github.com/example/research-agent/tree/main")]
    [InlineData("https://github.com/example/research-agent?tab=readme")]
    public void Normalize_RejectsUnsupportedUrls(string input)
    {
        Assert.Throws<AgentImportPreviewException>(() =>
            GitHubRepositoryUrlNormalizer.Normalize(input));
    }

    [Fact]
    public async Task PreviewAsync_PersistsImmutablePreviewAndWarnings()
    {
        await using var dbContext = CreateDbContext();
        var repositoryClient = new FakeGitHubAgentRepositoryClient(ValidManifest());
        var service = new AgentImportPreviewService(
            dbContext,
            repositoryClient,
            new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        Assert.Equal("Previewed", result.Status);
        Assert.Equal(FakeGitHubAgentRepositoryClient.CommitSha, result.CommitSha);
        Assert.Equal(64, result.ManifestDigest.Length);
        Assert.Equal("dotnet-project", result.RuntimeType);
        Assert.Contains(result.Warnings, warning => warning.Code == "network_access_requested");
        var configuration = Assert.Single(result.ConfigurationFields);
        Assert.Equal("workspaceId", configuration.Key);
        Assert.True(configuration.Required);
        var credential = Assert.Single(result.CredentialBindings);
        Assert.Equal("service-token", credential.Name);
        Assert.Contains("https://api.example.com", credential.AllowedOrigins);
        Assert.Single(await dbContext.AgentPackageSources.ToListAsync());
        var version = Assert.Single(await dbContext.AgentPackageVersions.ToListAsync());
        Assert.Equal(result.ImportId, version.Id);
        Assert.Equal(result.ManifestDigest, version.ManifestDigest);
    }

    [Fact]
    public async Task PreviewAsync_PreservesSelectOptionsAndDescription()
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            """{"key":"workspaceId","type":"string","label":"Workspace ID","required":true,"secret":false}""",
            """{"key":"responseTone","type":"select","label":"Response tone","required":true,"secret":false,"description":"Controls response detail.","options":[{"value":"concise","label":"Concise"},{"value":"balanced","label":"Balanced"}]}""",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        var field = Assert.Single(result.ConfigurationFields);
        Assert.Equal("Controls response detail.", field.Description);
        var options = Assert.IsAssignableFrom<IReadOnlyList<PluginConfigurationOption>>(field.Options);
        Assert.Equal(["concise", "balanced"], options.Select(option => option.Value).ToArray());
        Assert.Equal(["Concise", "Balanced"], options.Select(option => option.Label).ToArray());
    }

    [Fact]
    public async Task PreviewAsync_AcceptsLongRunningCapabilityWithinSdkLimit()
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            "\"executionTimeoutSeconds\":120",
            "\"executionTimeoutSeconds\":3600",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        Assert.Equal("Previewed", result.Status);
    }

    [Fact]
    public async Task PreviewAsync_RejectsSelectWithoutOptions()
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            """{"key":"workspaceId","type":"string","label":"Workspace ID","required":true,"secret":false}""",
            """{"key":"responseTone","type":"select","label":"Response tone","required":true,"secret":false}""",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewAsync(new PreviewAgentImportRequest(
                "https://github.com/example/research-agent")));

        Assert.Contains("must declare at least one option", exception.Message);
    }

    [Fact]
    public async Task PreviewAsync_RequiresExplicitBaselineGrantApproval()
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            "\"requires\": [{\"name\":\"documents.read.v1\",\"scope\":\"organization\"}]",
            $"\"requires\": [{{\"name\":\"documents.read.v1\",\"scope\":\"organization\"}},{{\"name\":\"{PlatformCapabilities.UserInputRequest}\",\"scope\":\"organization\"}}]",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        Assert.Equal(
            ["documents.read.v1", PlatformCapabilities.UserInputRequest],
            result.RequestedCapabilities);
    }

    [Fact]
    public async Task PreviewAsync_RejectsProjectPathTraversalWithoutPersisting()
    {
        await using var dbContext = CreateDbContext();
        var invalidManifest = ValidManifest().Replace(
            "src/ResearchAgent/ResearchAgent.csproj",
            "../ResearchAgent.csproj",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(invalidManifest),
            new TestAuditEventWriter());

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewAsync(new PreviewAgentImportRequest(
                "https://github.com/example/research-agent")));

        Assert.Contains("without parent traversal", exception.Message);
        Assert.Empty(await dbContext.AgentPackageSources.ToListAsync());
        Assert.Empty(await dbContext.AgentPackageVersions.ToListAsync());
    }

    [Fact]
    public async Task PreviewAsync_RejectsConfigurableAgentWithoutCanonicalConfigurationCapabilities()
    {
        await using var dbContext = CreateDbContext();
        var invalidManifest = ValidManifest()
            .Replace("agent.configuration.describe.v1", "plugin.configuration.describe.v1", StringComparison.Ordinal)
            .Replace("agent.configuration.update.v1", "plugin.configuration.update.v1", StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(invalidManifest),
            new TestAuditEventWriter());

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewAsync(new PreviewAgentImportRequest(
                "https://github.com/example/research-agent")));

        Assert.Contains("agent.configuration.describe.v1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("agent.configuration.update.v1", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await dbContext.AgentPackageVersions.ToListAsync());
    }

    [Fact]
    public async Task PreviewAsync_ReusesExistingImmutablePreview()
    {
        await using var dbContext = CreateDbContext();
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(ValidManifest()),
            new TestAuditEventWriter());
        var request = new PreviewAgentImportRequest("https://github.com/example/research-agent");

        var first = await service.PreviewAsync(request);
        var second = await service.PreviewAsync(request);

        Assert.Equal(first.ImportId, second.ImportId);
        Assert.Single(await dbContext.AgentPackageSources.ToListAsync());
        Assert.Single(await dbContext.AgentPackageVersions.ToListAsync());
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CSweetDbContext(options);
    }

    private static string ValidManifest() => """
        {
          "manifestVersion": "2.0",
          "kind": "agent",
          "id": "com.example.research-agent",
          "name": "Research Agent",
          "version": "1.2.3",
          "publisher": { "id": "com.example", "name": "Example" },
          "runtime": {
            "type": "dotnet-project",
            "projectPath": "src/ResearchAgent/ResearchAgent.csproj",
            "targetFramework": "net10.0",
            "defaultActivationMode": "Periodic"
          },
          "protocol": { "minimumVersion": "2.0", "maximumVersion": "2.x" },
          "provides": [
            {"name":"research.execute.v1","description":"Execute research","inputSchema":{"type":"object","additionalProperties":false},"outputSchema":{"type":"object"},"executionTimeoutSeconds":120,"idempotency":"work-item"},
            {"name":"agent.configuration.describe.v1","description":"Describe configuration","inputSchema":{"type":"object","additionalProperties":false},"outputSchema":{"type":"object"},"executionTimeoutSeconds":30,"idempotency":"work-item"},
            {"name":"agent.configuration.update.v1","description":"Update configuration","inputSchema":{"type":"object"},"outputSchema":{"type":"object"},"executionTimeoutSeconds":30,"idempotency":"caller-key"}
          ],
          "requires": [{"name":"documents.read.v1","scope":"organization"}],
          "events": {
            "subscribes": ["research.requested.v1"]
          },
          "configuration": [
            {"key":"workspaceId","type":"string","label":"Workspace ID","required":true,"secret":false}
          ],
          "credentials": [
            {"name":"service-token","type":"authorization-header","allowedOrigins":["https://api.example.com"]}
          ],
          "webAccess": {
            "mode": "Allowlist",
            "rules": [{"scheme":"https","host":"api.example.com","pathPrefix":"/","methods":["GET"],"protocol":"http","purpose":"Research","credential":"service-token"}]
          }
        }
        """;

    private sealed class FakeGitHubAgentRepositoryClient : IGitHubAgentRepositoryClient
    {
        public const string CommitSha = "0123456789abcdef0123456789abcdef01234567";
        private readonly byte[] _manifest;

        public FakeGitHubAgentRepositoryClient(string manifest)
        {
            _manifest = Encoding.UTF8.GetBytes(manifest);
        }

        public Task<string> GetDefaultBranchAsync(
            string repositoryOwner,
            string repositoryName,
            CancellationToken cancellationToken) => Task.FromResult("main");

        public Task<string> ResolveCommitShaAsync(
            string repositoryOwner,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken) => Task.FromResult(CommitSha);

        public Task<byte[]> GetRootManifestAsync(
            string repositoryOwner,
            string repositoryName,
            string commitSha,
            CancellationToken cancellationToken) => Task.FromResult(_manifest);
    }
}
