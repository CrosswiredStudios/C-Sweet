using System.Text.Json;
using System.IO.Compression;
using CSweet.Agent.SDK;
using CSweet.Application.Agents;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Agents;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentCatalogServiceTests
{
    [Fact]
    public async Task Aggregate_DeduplicatesByAgentIdAndPrefersFirstPartyRepositorySource()
    {
        var firstParty = Agent("first-party:1", AgentCatalogSource.FirstPartyCatalog);
        var local = Agent("local:1", AgentCatalogSource.LocalDirectory) with
        {
            RoleKey = null,
            RoleName = null,
            LicenseSpdxId = null,
            IconUrls = []
        };
        var service = new AgentCatalogService(
            [
                new StubProvider(AgentCatalogSource.FirstPartyCatalog, firstParty),
                new StubProvider(AgentCatalogSource.LocalDirectory, local)
            ],
            NullLogger<AgentCatalogService>.Instance);

        var result = await service.GetAvailableAgentsAsync(
            null,
            new("Product Manager", RequiredCapabilities: ["product.strategy"]));

        var agent = Assert.Single(result.Agents);
        Assert.Equal(AgentCatalogSource.FirstPartyCatalog, agent.Source);
        Assert.Contains(AgentCatalogSource.LocalDirectory, agent.AlternateSources);
        Assert.Equal("https://github.com/example/product-manager", agent.RepositoryUrl);
        Assert.Equal("com.csweet.product-manager", agent.AgentId);
        Assert.Equal("product-manager", agent.RoleKey);
        Assert.Equal("Product Manager", agent.RoleName);
    }

    [Fact]
    public async Task CanonicalRole_MatchesAgentWithSpecificDisplayName()
    {
        var candidate = Agent("first-party:1", AgentCatalogSource.FirstPartyCatalog) with
        {
            Name = "Software PM",
            RoleKey = "product-manager",
            RoleName = "Product Manager",
            RoleAliases = ["Software Product Manager", "Software PM"]
        };
        var service = new AgentCatalogService(
            [new StubProvider(AgentCatalogSource.FirstPartyCatalog, candidate)],
            NullLogger<AgentCatalogService>.Instance);

        var result = await service.GetAvailableAgentsAsync(null, new(Role: "Product Manager"));

        var agent = Assert.Single(result.Agents);
        Assert.Equal("Software PM", agent.Name);
        Assert.Equal("product-manager", agent.RoleKey);
        Assert.Equal("Product Manager", agent.RoleName);
    }

    [Fact]
    public async Task RoleCategory_IsRequiredWhileSpecializationOnlyImprovesRanking()
    {
        var gameSpecialist = Agent("first-party:game", AgentCatalogSource.FirstPartyCatalog) with
        {
            Name = "Software Architect with game experience",
            AgentId = "com.example.game-architect",
            RoleCategoryKeys = ["software-architect"],
            SpecializationKeys = ["game-development"]
        };
        var generalArchitect = Agent("first-party:general", AgentCatalogSource.FirstPartyCatalog) with
        {
            Name = "Software Architect",
            AgentId = "com.example.general-architect",
            RoleCategoryKeys = ["software-architect"],
            SpecializationKeys = ["distributed-systems"]
        };
        var gameDeveloper = Agent("first-party:developer", AgentCatalogSource.FirstPartyCatalog) with
        {
            Name = "Game Developer",
            AgentId = "com.example.game-developer",
            RoleCategoryKeys = ["software-developer"],
            SpecializationKeys = ["game-development"]
        };
        var service = new AgentCatalogService(
            [new StubProvider(AgentCatalogSource.FirstPartyCatalog, generalArchitect, gameDeveloper, gameSpecialist)],
            NullLogger<AgentCatalogService>.Instance);

        var result = await service.GetAvailableAgentsAsync(null, new(
            RoleCategoryKey: "software-architect",
            PreferredSpecializationKeys: ["game-development"]));

        Assert.Equal(2, result.Agents.Count);
        Assert.Equal(gameSpecialist.Name, result.Agents[0].Name);
        Assert.Contains(result.Agents, x => x.Name == generalArchitect.Name);
        Assert.DoesNotContain(result.Agents, x => x.Name == gameDeveloper.Name);
    }

    [Fact]
    public async Task LocalDirectory_DiscoversManifestWithoutExposingPathOrExecutingSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"csweet-agent-catalog-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "ProductManager");
        var serviceFolder = Path.Combine(root, "CommunicationService");
        Directory.CreateDirectory(Path.Combine(folder, "src", "ProductManager"));
        Directory.CreateDirectory(serviceFolder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "csweet-plugin.json"), Manifest());
            await File.WriteAllTextAsync(
                Path.Combine(serviceFolder, "csweet-plugin.json"),
                Manifest("Communication Service").Replace("\"kind\": \"agent\"", "\"kind\": \"service\"", StringComparison.Ordinal));
            await File.WriteAllTextAsync(Path.Combine(folder, "src", "ProductManager", "ProductManager.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            await File.WriteAllTextAsync(Path.Combine(folder, ".env"), "SECRET=do-not-copy");
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            await File.WriteAllTextAsync(Path.Combine(folder, "bin", "compiled.dll"), "not-source");
            var provider = new LocalDirectoryAgentCatalogProvider(
                new TestEnvironment(root),
                Options.Create(new AgentCatalogOptions { LocalDirectoryPath = "." }),
                new PluginManifestReader());

            var result = await provider.SearchAsync(null, new(Role: "Product Manager"));

            var agent = Assert.Single(result.Agents);
            Assert.True(result.Health.Available);
            Assert.Null(result.Health.Message);
            Assert.Equal(AgentCatalogSource.LocalDirectory, agent.Source);
            Assert.StartsWith("local:com.csweet.product-manager:", agent.AgentReference);
            Assert.DoesNotContain(root, JsonSerializer.Serialize(agent), StringComparison.OrdinalIgnoreCase);
            Assert.Null(agent.RepositoryUrl);
            Assert.Equal(["product.strategy", "product.discovery"], agent.Capabilities);
            Assert.Equal("product-manager", agent.RoleKey);
            Assert.Equal("Product Manager", agent.RoleName);
            Assert.Equal("MIT", agent.LicenseSpdxId);
            Assert.Contains("https://example.com/product-manager.png", agent.IconUrls!);

            var snapshot = await provider.CreateArchiveAsync(agent.AgentReference);
            using var stream = new MemoryStream(snapshot.Content);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, x => x.FullName == "csweet-plugin.json");
            Assert.DoesNotContain(archive.Entries, x => x.FullName.Contains(".env", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(archive.Entries, x => x.FullName.StartsWith("bin/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstalledProvider_IsOrganizationScoped()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        db.AgentInstallations.AddRange(
            Installation(organizationId, "Visible Agent"),
            Installation(otherOrganizationId, "Hidden Agent"));
        await db.SaveChangesAsync();
        var provider = new InstalledAgentCatalogProvider(db);

        var result = await provider.SearchAsync(organizationId, new());

        var agent = Assert.Single(result.Agents);
        Assert.Equal("Visible Agent", agent.Name);
        Assert.Equal(AgentAvailabilityState.InstalledEnabled, agent.Availability);
    }

    private static AvailableAgent Agent(string reference, AgentCatalogSource source) => new(
        reference,
        "com.csweet.product-manager",
        source,
        [],
        AgentAvailabilityState.AvailableToInstall,
        null,
        "Product Manager",
        "Owns product strategy.",
        "C-Sweet",
        "Product",
        ["Product Manager"],
        ["product"],
        ["product.strategy"],
        null,
        "USD",
        null,
        0,
        null,
        source == AgentCatalogSource.FirstPartyCatalog ? "https://github.com/example/product-manager" : null,
        0.8m,
        "Test",
        "product-manager",
        "Product Manager",
        "MIT",
        null,
        ["https://example.com/product-manager.png"]);

    private static AgentInstallation Installation(Guid organizationId, string name)
    {
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = $"https://github.com/example/{name.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}",
            RepositoryOwner = "example",
            RepositoryName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = source.Id,
            PackageSource = source,
            AgentId = $"com.example.{name.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant()}",
            AgentName = name,
            Version = "1.0.0",
            PublisherId = "com.example",
            PublisherName = "Example",
            PluginKind = PluginKind.Agent,
            ManifestJson = Manifest(name),
            CommitSha = new('a', 40),
            ManifestDigest = new('b', 64),
            RuntimeType = "dotnet-project",
            ProjectPath = "src/ProductManager/ProductManager.csproj",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var installationId = Guid.NewGuid();
        return new AgentInstallation
        {
            Id = installationId,
            PackageVersion = package,
            PackageVersionId = package.Id,
            BusinessId = organizationId.ToString("D"),
            IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active,
            Grant = new AgentInstallationGrant
            {
                Id = Guid.NewGuid(),
                AgentInstallationId = installationId,
                ProvidedCapabilitiesJson = "[\"product.strategy\"]",
                ApprovedAt = DateTimeOffset.UtcNow
            }
        };
    }

    private static string Manifest(string name = "Product Manager") => $$"""
    {
      "manifestVersion": "2.0",
      "kind": "agent",
      "rolePolicy": { "profile": "manager.v1", "declaredRoleKeys": ["software-product-manager"], "specializationKeys": ["software-delivery"] },
      "id": "com.csweet.product-manager",
      "name": "{{name}}",
      "version": "1.0.0",
      "publisher": { "id": "com.csweet", "name": "C-Sweet" },
      "runtime": {
        "type": "dotnet-project",
        "projectPath": "src/ProductManager/ProductManager.csproj",
        "targetFramework": "net10.0",
        "defaultActivationMode": "OnDemand",
        "maximumConcurrentJobs": 1
      },
      "protocol": { "minimumVersion": "2.0", "maximumVersion": "2.x" },
      "provides": [
        { "name": "product.strategy", "description": "Create product strategy", "inputSchema": { "type": "object" }, "outputSchema": { "type": "object" }, "executionTimeoutSeconds": 120, "idempotency": "work-item" },
        { "name": "product.discovery", "description": "Run product discovery", "inputSchema": { "type": "object" }, "outputSchema": { "type": "object" }, "executionTimeoutSeconds": 120, "idempotency": "work-item" }
      ],
      "requires": [],
      "events": { "subscribes": [] },
      "catalog": {
        "summary": "Owns product outcomes.",
        "category": "Product",
        "role": { "key": "product-manager", "name": "Product Manager" },
        "license": { "spdxId": "MIT" },
        "iconUrls": [ "https://example.com/product-manager.png" ],
        "roleAliases": [ "Product Manager" ],
        "keywords": [ "roadmap" ]
      }
    }
    """;

    private sealed class StubProvider(AgentCatalogSource source, params AvailableAgent[] agents)
        : IAgentCatalogProvider
    {
        public AgentCatalogSource Source => source;

        public Task<AgentCatalogProviderResult> SearchAsync(
            Guid? organizationId,
            AvailableAgentSearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCatalogProviderResult(agents, new(source, true)));

        public Task<AvailableAgent?> ResolveAsync(
            Guid? organizationId,
            string agentReference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(agents.FirstOrDefault(x => x.AgentReference == agentReference));
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
