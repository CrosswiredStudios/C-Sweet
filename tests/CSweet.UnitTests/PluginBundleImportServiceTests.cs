using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Setup;
using CSweet.ExecutionArtifacts;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class PluginBundleImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csweet-plugin-bundle-tests", Guid.NewGuid().ToString("N"));
    private readonly CSweetDbContext _db = new(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task PreviewVerifiesExactBytesButDoesNotInstallOrGrantExecution_ThenApprovalSkipsSourceBuild()
    {
        var service = Service();
        var bytes = Bundle();
        var preview = await service.PreviewAsync(new MemoryStream(bytes), "test.cspb");
        Assert.Equal("Service", preview.PluginKind);
        Assert.Equal("PluginBundle", preview.PackageSourceMode);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), preview.BundleDigest);
        var package = await _db.AgentPackageVersions.SingleAsync();
        Assert.Equal(preview.BundleDigest, package.PackageDigest);
        Assert.NotEmpty(package.ArtifactSignature!);
        Assert.Empty(_db.AgentInstallations);
        Assert.Empty(_db.AgentInstallationGrants);
        Assert.Empty(_db.AgentBuildJobs);
        Assert.Empty(_db.AgentRuntimeInstances);

        _db.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
        {
            Id = Guid.NewGuid(), EnableImportedAgents = true, AllowAlwaysOnCommunityAgents = true, MinimumTickFrequencySeconds = 10,
            MaximumWorkloadMemoryMb = 4096, MaximumWorkloadCpuPercent = 200, UpdatedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();
        IPluginInstallationService installations = new AgentInstallationService(_db, new TestAuditEventWriter(), null!, null!,
            Options.Create(new AgentRuntimeManagerOptions()), NullLogger<AgentInstallationService>.Instance);
        var installed = await installations.InstallAsync(preview.ImportId,
            new InstallAgentRequest("enterprise", "AlwaysOn", 300, "Skip", [], [], [], [], [], 300, 512, 50) { PluginScope = "System" });
        Assert.Equal("System", installed.InstallationScope);
        Assert.Single(await installations.ListAsync());
        Assert.Single(_db.AgentInstallationGrants);
        Assert.Empty(_db.AgentBuildJobs);
        Assert.Empty(_db.AgentRuntimeInstances);
        package.PluginKind = PluginKind.Agent;
        await _db.SaveChangesAsync();
        Assert.Empty(await installations.ListAsync());
    }

    [Fact]
    public async Task RepeatedIdenticalUploadWithDifferentNameReusesImmutablePackage()
    {
        var service = Service();
        var bytes = Bundle();
        var first = await service.PreviewAsync(new MemoryStream(bytes), "test.cspb");
        var second = await service.PreviewAsync(new MemoryStream(bytes), "renamed.CSPB");
        Assert.Equal(first.ImportId, second.ImportId);
        Assert.Single(_db.AgentPackageSources);
        Assert.Single(_db.AgentPackageVersions);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging")));
    }

    [Theory]
    [InlineData("test.csab")]
    [InlineData("test.zip")]
    [InlineData("../test.cspb")]
    [InlineData("C:\\test.cspb")]
    public async Task RejectsOtherPackageTypesAndPaths(string name)
    {
        await Assert.ThrowsAsync<AgentImportPreviewException>(() => Service().PreviewAsync(new MemoryStream(Bundle()), name));
        Assert.Empty(_db.AgentPackageVersions);
    }

    [Theory]
    [InlineData("payload/../escape", "traversal")]
    [InlineData("payload/csweet-plugin.json", "duplicate")]
    [InlineData("outside", "outside")]
    public async Task ArtifactGateRejectsUnsafePayloadBeforeDatabasePersistence(string name, string _)
    {
        await Assert.ThrowsAsync<AgentImportPreviewException>(() => Service().PreviewAsync(new MemoryStream(Bundle(extra: name)), "test.cspb"));
        Assert.Empty(_db.AgentPackageVersions);
        Assert.Empty(_db.AgentPackageSources);
    }

    [Fact]
    public async Task RejectsAgentsAndMalformedBundlesWithoutCreatingInstallations()
    {
        var exception = await Assert.ThrowsAsync<AgentImportPreviewException>(() => Service().PreviewAsync(new MemoryStream(Bundle(kind: "agent")), "agent.cspb"));
        Assert.Contains("Installed Agents", exception.Message);
        await Assert.ThrowsAsync<AgentImportPreviewException>(() => Service().PreviewAsync(new MemoryStream("not a tar"u8.ToArray()), "test.cspb"));
        Assert.Empty(_db.AgentInstallations);
        Assert.Empty(_db.AgentPackageVersions);
    }

    [Fact]
    public async Task RejectsMissingManifestAndNonExecutableEntrypoint()
    {
        await Assert.ThrowsAsync<AgentImportPreviewException>(() => Service().PreviewAsync(new MemoryStream(Bundle(includeManifest: false)), "test.cspb"));
        await Assert.ThrowsAsync<AgentImportPreviewException>(() => Service().PreviewAsync(new MemoryStream(Bundle(executable: false)), "test.cspb"));
        Assert.Empty(_db.AgentPackageVersions);
    }

    [Fact]
    public async Task PreservesConnectorSetupAndConnectionMetadata()
    {
        var manifest = """
            {"manifestVersion":"2.0","kind":"connector","id":"com.example.connection","name":"Connection","version":"1.0.0",
             "publisher":{"id":"com.example","name":"Example"},
             "runtime":{"type":"dotnet-project","projectPath":"src/Test/Test.csproj","targetFramework":"net10.0","defaultActivationMode":"OnDemand","supportsMultipleInstallations":true,"maximumConcurrentJobs":1},
             "protocol":{"minimumVersion":"2.1","maximumVersion":"2.x"},"requires":[],
             "provides":[{"name":"test.read.v1","description":"Read test data","inputSchema":{"type":"object","properties":{},"additionalProperties":false},"outputSchema":{"type":"object"},"executionTimeoutSeconds":30,"idempotency":"none"}],
             "providerOperations":[{"capability":"test.read.v1","description":"Read test data","effect":"read","idempotency":"none",
               "inputSchema":{"type":"object","properties":{},"additionalProperties":false},"outputSchema":{"type":"object"},
               "http":{"connection":"provider","scopeSets":["base"],"endpoint":"https://api.example.com/test","method":"GET"}}],
             "connections":[{"id":"provider","type":"oauth2","providerProfile":"profile","allowedOrigins":["https://api.example.com"],
                "provider":{"displayName":"Example","authorizationEndpoint":"https://id.example.com/auth","tokenEndpoint":"https://id.example.com/token","revocationEndpoint":"https://id.example.com/revoke","clientAuthentication":"client_secret_post"},
                "scopeSets":[{"id":"base","label":"Read","purpose":"Read","required":true,"scopes":["scope.read"]}]}],
             "setup":{"required":true,"entryFlow":"onboarding","flows":[{"id":"onboarding","title":"Setup",
                "steps":[{"id":"connect","kind":"oauth-connect","title":"Connect","connection":"provider","scopeSet":"base"}]}]}}
            """;
        var preview = await Service().PreviewAsync(new MemoryStream(Bundle(manifest: manifest)), "connection.cspb");
        Assert.Equal("Connector", preview.PluginKind);
        Assert.NotEmpty(preview.Connections);
        Assert.NotNull(preview.Setup);
    }

    private PluginBundleImportService Service()
    {
        var artifacts = new FileSystemAgentArtifactStore(new ArtifactStoreOptions { RootPath = Path.Combine(_root, "artifacts") },
            new HmacAgentArtifactSigner(Convert.ToBase64String(new byte[32])));
        var previews = new AgentImportPreviewService(_db, null!, new TestAuditEventWriter());
        return new(_db, new PluginManifestReader(), artifacts, previews, new TestAuditEventWriter(),
            Options.Create(new AgentRuntimeManagerOptions { SourceArchiveStorePath = Path.Combine(_root, "staging") }));
    }

    private static byte[] Bundle(string kind = "service", string? extra = null, bool executable = true, bool includeManifest = true, string? manifest = null)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            Add(writer, "artifact.json", """{"formatVersion":"1.0","operatingSystem":"linux","architecture":"x64","entrypoint":["test"]}""");
            Add(writer, "payload/test", "binary", executable);
            if (includeManifest) Add(writer, "payload/csweet-plugin.json", manifest ?? $$$"""
                {"manifestVersion":"2.0","kind":"{{{kind}}}","id":"com.example.test","name":"Test Plugin","version":"1.0.0",
                 "publisher":{"id":"com.example","name":"Example"},
                 "runtime":{"type":"dotnet-project","projectPath":"src/Test/Test.csproj","targetFramework":"net10.0","defaultActivationMode":"AlwaysOn","maximumConcurrentJobs":1},
                 "protocol":{"minimumVersion":"2.0","maximumVersion":"2.x"},
                 "catalog":{"role":{"key":"test-agent","name":"Test Agent"},"license":{"spdxId":"MIT"}},
                 "provides":[],"requires":[],"events":{"subscribes":[]},"configuration":[],"credentials":[],"webAccess":{"mode":"None","rules":[]},"ui":[]}
                """);
            if (extra is not null) Add(writer, extra, "hostile");
        }
        return output.ToArray();
    }

    private static void Add(TarWriter writer, string name, string text, bool executable = false)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = input, Uid = 0, Gid = 0,
            Mode = (UnixFileMode)Convert.ToInt32(executable ? "755" : "644", 8)
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
