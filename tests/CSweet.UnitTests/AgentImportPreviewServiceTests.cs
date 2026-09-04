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
            """{"key":"responseTone","type":"select","label":"Response tone","required":true,"secret":false,"description":"Controls response detail.","defaultValue":"balanced","options":[{"value":"concise","label":"Concise"},{"value":"balanced","label":"Balanced"}]}""",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        var field = Assert.Single(result.ConfigurationFields);
        Assert.Equal("Controls response detail.", field.Description);
        Assert.Equal("balanced", field.DefaultValue?.GetString());
        var options = Assert.IsAssignableFrom<IReadOnlyList<PluginConfigurationOption>>(field.Options);
        Assert.Equal(["concise", "balanced"], options.Select(option => option.Value).ToArray());
        Assert.Equal(["Concise", "Balanced"], options.Select(option => option.Label).ToArray());
    }

    [Fact]
    public async Task PreviewAsync_PreservesConditionalVisibilityMetadata()
    {
        await using var dbContext = CreateDbContext();
        var manifest = WithConfiguration(
            """{"key":"profile","type":"select","label":"Profile","required":true,"secret":false,"defaultValue":"general","options":[{"value":"general","label":"General"},{"value":"custom","label":"Custom"}]},{"key":"description","type":"textarea","label":"Description","required":true,"secret":false,"visibleWhenFieldKey":"profile","visibleWhenValue":"custom"}""");
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        var description = result.ConfigurationFields.Single(field => field.Key == "description");
        Assert.Equal("profile", description.VisibleWhenFieldKey);
        Assert.Equal("custom", description.VisibleWhenValue);
    }

    [Fact]
    public async Task PreviewAsync_RejectsInvalidConditionalVisibilityMetadata()
    {
        var invalidConfigurations = new (string Configuration, string Expected)[]
        {
            ("""{"key":"profile","type":"text","label":"Profile","required":false,"secret":false},{"key":"description","type":"text","label":"Description","required":false,"secret":false,"visibleWhenFieldKey":"profile"}""",
                "must declare visibleWhenFieldKey and visibleWhenValue together"),
            ("""{"key":"profile","type":"text","label":"Profile","required":false,"secret":false},{"key":"description","type":"text","label":"Description","required":false,"secret":false,"visibleWhenFieldKey":"missing","visibleWhenValue":"custom"}""",
                "references unknown visibility field 'missing'"),
            ("""{"key":"profile","type":"text","label":"Profile","required":false,"secret":false,"visibleWhenFieldKey":"profile","visibleWhenValue":"custom"}""",
                "cannot control its own visibility"),
            ("""{"key":"profile","type":"select","label":"Profile","required":false,"secret":false,"options":[{"value":"general","label":"General"}]},{"key":"description","type":"text","label":"Description","required":false,"secret":false,"visibleWhenFieldKey":"profile","visibleWhenValue":"custom"}""",
                "visibility value is not declared"),
            ("""{"key":"first","type":"text","label":"First","required":false,"secret":false,"visibleWhenFieldKey":"second","visibleWhenValue":"yes"},{"key":"second","type":"text","label":"Second","required":false,"secret":false,"visibleWhenFieldKey":"first","visibleWhenValue":"yes"}""",
                "Configuration visibility cycle")
        };

        foreach (var (configuration, expected) in invalidConfigurations)
        {
            await using var dbContext = CreateDbContext();
            var service = new AgentImportPreviewService(
                dbContext,
                new FakeGitHubAgentRepositoryClient(WithConfiguration(configuration)),
                new TestAuditEventWriter());

            var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
                service.PreviewAsync(new PreviewAgentImportRequest(
                    "https://github.com/example/research-agent")));
            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        }
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

    [Theory]
    [InlineData("Manual")]
    [InlineData("Periodic")]
    [InlineData("Unknown")]
    public async Task PreviewAsync_RejectsRemovedAndUnknownActivationModes(string activationMode)
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            "\"defaultActivationMode\": \"Scheduled\"",
            $"\"defaultActivationMode\": \"{activationMode}\"",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewAsync(new PreviewAgentImportRequest(
                "https://github.com/example/research-agent")));

        Assert.Contains("AlwaysOn, OnDemand, or Scheduled", exception.Message);
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
    public async Task PreviewAsync_RejectsConfigurationDependencyOnUnknownField()
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            "\"secret\":false}",
            "\"secret\":false,\"dependsOnFieldKey\":\"missingProvider\"}",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext,
            new FakeGitHubAgentRepositoryClient(manifest),
            new TestAuditEventWriter());

        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewAsync(new PreviewAgentImportRequest(
                "https://github.com/example/research-agent")));

        Assert.Contains("depends on unknown field 'missingProvider'", exception.Message);
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
    public async Task PreviewAsync_AcceptsSupportedRolePolicyProfile()
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            "\"rolePolicy\": { \"profile\": \"individual-contributor.v1\", \"declaredRoleKeys\": [\"researcher\"], \"specializationKeys\": [\"business-research\"] },",
            "\"rolePolicy\": { \"profile\": \"manager.v1\", \"declaredRoleKeys\": [\"research-manager\"] },",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext, new FakeGitHubAgentRepositoryClient(manifest), new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest(
            "https://github.com/example/research-agent"));

        Assert.Equal("Previewed", result.Status);
    }

    [Theory]
    [InlineData("manager.v2", "research-manager")]
    [InlineData("manager.v1", "")]
    public async Task PreviewAsync_RejectsInvalidRolePolicy(string profile, string roleKey)
    {
        await using var dbContext = CreateDbContext();
        var manifest = ValidManifest().Replace(
            "\"rolePolicy\": { \"profile\": \"individual-contributor.v1\", \"declaredRoleKeys\": [\"researcher\"], \"specializationKeys\": [\"business-research\"] },",
            $"\"rolePolicy\": {{ \"profile\": \"{profile}\", \"declaredRoleKeys\": [\"{roleKey}\"] }},",
            StringComparison.Ordinal);
        var service = new AgentImportPreviewService(
            dbContext, new FakeGitHubAgentRepositoryClient(manifest), new TestAuditEventWriter());

        await Assert.ThrowsAsync<AgentImportPreviewException>(() =>
            service.PreviewAsync(new PreviewAgentImportRequest(
                "https://github.com/example/research-agent")));
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
        Assert.Empty(await dbContext.AgentPackageSources.ToListAsync());
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

    [Fact]
    public async Task PreviewAsync_PreservesSafeSetupAndProgressiveConnections()
    {
        await using var dbContext = CreateDbContext();
        var service = new AgentImportPreviewService(dbContext,
            new FakeGitHubAgentRepositoryClient(ConnectedManifest("permission-summary")), new TestAuditEventWriter());

        var result = await service.PreviewAsync(new PreviewAgentImportRequest("https://github.com/example/research-agent"));

        Assert.Equal("onboarding", result.Setup?.EntryFlow);
        var connection = Assert.Single(result.Connections);
        Assert.Equal("com.example.provider", connection.ProviderProfile);
        Assert.Equal(["base", "publish"], connection.ScopeSets.Select(x => x.Id));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("javascript")]
    [InlineData("iframe")]
    [InlineData("razor")]
    public void ValidateManifest_RejectsExecutablePluginSetupUi(string kind)
    {
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(ConnectedManifest(kind),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var exception = Assert.Throws<AgentImportPreviewException>(() => AgentImportPreviewService.ValidateManifest(manifest));

        Assert.Contains("unsafe or unsupported", exception.Message);
    }

    [Fact]
    public void ValidateManifest_AcceptsExactMcpAndConfinedFileTransferDeclarations()
    {
        var json = ConnectedManifest("permission-summary")
            .Replace("\"credentials\":[]", "\"credentials\":[{\"name\":\"sftp\",\"type\":\"username-password-host-key\",\"allowedOrigins\":[]}]", StringComparison.Ordinal)
            .Replace("\"connections\":[", "\"mcpServers\":[{\"id\":\"namecheap\",\"endpoint\":\"https://mcp.namecheap.com/mcp\",\"transport\":\"streamable-http\",\"connection\":\"provider\",\"protocolVersions\":[\"2025-06-18\"],\"tools\":[{\"capability\":\"namecheap.domains.list.v1\",\"remoteName\":\"domains_list\",\"description\":\"List domains\",\"inputSchema\":{\"type\":\"object\"},\"outputSchema\":{\"type\":\"object\"},\"descriptorHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"effect\":\"read\"}]}],\"fileTransferTargets\":[{\"id\":\"shared-hosting\",\"protocol\":\"sftp\",\"credential\":\"sftp\",\"allowedHostSuffixes\":[\".web-hosting.com\"],\"port\":21098,\"rootPath\":\"public_html\",\"operations\":[\"probe\",\"list\",\"stat\",\"upload\"]}],\"connections\":[", StringComparison.Ordinal);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        AgentImportPreviewService.ValidateManifest(manifest);

        Assert.Single(manifest.McpServers);
        Assert.Equal(21098, Assert.Single(manifest.FileTransferTargets).Port);
    }

    [Fact]
    public void ValidateManifest_RejectsBroadFileTransferRoot()
    {
        var json = ConnectedManifest("permission-summary")
            .Replace("\"credentials\":[]", "\"credentials\":[{\"name\":\"sftp\",\"type\":\"username-password-host-key\",\"allowedOrigins\":[]}]", StringComparison.Ordinal)
            .Replace("\"connections\":[", "\"fileTransferTargets\":[{\"id\":\"unsafe\",\"protocol\":\"sftp\",\"credential\":\"sftp\",\"allowedHostSuffixes\":[\".example.com\"],\"port\":22,\"rootPath\":\"/\",\"operations\":[\"upload\"]}],\"connections\":[", StringComparison.Ordinal);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var exception = Assert.Throws<AgentImportPreviewException>(() =>
            AgentImportPreviewService.ValidateManifest(manifest));

        Assert.Contains("rootPath", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CSweetDbContext(options);
    }

    private static string WithConfiguration(string configuration) => ValidManifest().Replace(
        """{"key":"workspaceId","type":"string","label":"Workspace ID","required":true,"secret":false}""",
        configuration,
        StringComparison.Ordinal);

    private static string ValidManifest() => """
        {
          "manifestVersion": "2.0",
          "kind": "agent",
          "rolePolicy": { "profile": "individual-contributor.v1", "declaredRoleKeys": ["researcher"], "specializationKeys": ["business-research"] },
          "id": "com.example.research-agent",
          "name": "Research Agent",
          "version": "1.2.3",
          "publisher": { "id": "com.example", "name": "Example" },
          "runtime": {
            "type": "dotnet-project",
            "projectPath": "src/ResearchAgent/ResearchAgent.csproj",
            "targetFramework": "net10.0",
            "defaultActivationMode": "Scheduled"
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
          },
          "catalog": {
            "role": { "key": "researcher", "name": "Researcher" },
            "license": { "spdxId": "MIT" },
            "iconUrls": ["https://example.com/research-agent.png"]
          }
        }
        """;

    private static string ConnectedManifest(string firstStepKind) => $$"""
        {
          "manifestVersion":"2.0","kind":"agent","rolePolicy":{"profile":"individual-contributor.v1","declaredRoleKeys":["connected-agent"],"specializationKeys":[]},"id":"com.example.connected","name":"Connected","version":"1.0.0",
          "publisher":{"id":"com.example","name":"Example"},
          "runtime":{"type":"dotnet-project","projectPath":"src/Connected/Connected.csproj","targetFramework":"net10.0","defaultActivationMode":"OnDemand","supportsMultipleInstallations":true,"maximumConcurrentJobs":1,"workspaceAccess":"None"},
          "protocol":{"minimumVersion":"2.0","maximumVersion":"2.x"},
          "provides":[{"name":"example.setup.validate.v1","description":"Validate setup","inputSchema":{"type":"object"},"outputSchema":{"type":"object"},"executionTimeoutSeconds":30,"idempotency":"none"}],
          "requires":[],"events":{"subscribes":[]},"configuration":[],"credentials":[],
          "connections":[{"id":"provider","type":"oauth2","providerProfile":"com.example.provider","allowedOrigins":["https://api.example.com"],"scopeSets":[
            {"id":"base","label":"Read","purpose":"Read account","required":true,"scopes":["account.read"]},
            {"id":"publish","label":"Publish","purpose":"Publish content","required":false,"scopes":["content.write"]}
          ]}],
          "setup":{"required":true,"entryFlow":"onboarding","flows":[{"id":"onboarding","title":"Connect","steps":[
            {"id":"intro","kind":"{{firstStepKind}}","title":"Review"},
            {"id":"connect","kind":"oauth-connect","title":"Connect","connection":"provider","scopeSet":"base"},
            {"id":"health","kind":"health-check","title":"Validate","capability":"example.setup.validate.v1"}
          ]}]},
          "webAccess":{"mode":"Allowlist","rules":[{"scheme":"https","host":"api.example.com","pathPrefix":"/","methods":["GET"],"protocol":"http","purpose":"Provider API","connection":"provider"}]},
          "ui":[],"catalog":{"role":{"key":"connected-agent","name":"Connected Agent"},"license":{"spdxId":"MIT"},"iconUrls":[]}
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
