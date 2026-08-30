using System.Text;
using System.Text.Json;
using CSweet.AgentBroker;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.WorkManagement;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class DeliveryEvidenceSafetyTests
{
    private static readonly JsonElement OutputPolicy = JsonSerializer.SerializeToElement(new
    {
        maximumFileCount = 2,
        maximumFileBytes = 100,
        maximumTotalBytes = 150,
        denySymlinks = true,
        denyAbsolutePaths = true
    });

    [Fact]
    public void OutputManifestEnforcesContainmentUniquenessHashesTypesAndBounds()
    {
        var valid = Entry("dist/game.js", 64, "application/javascript");
        DeliveryEvidenceCapabilityHandler.ValidateOutputs([valid], OutputPolicy, ["application/javascript"]);

        Assert.Throws<ArgumentException>(() => DeliveryEvidenceCapabilityHandler.ValidateOutputs(
            [Entry("../escape.js", 1, "application/javascript")], OutputPolicy, ["application/javascript"]));
        Assert.Throws<ArgumentException>(() => DeliveryEvidenceCapabilityHandler.ValidateOutputs(
            [valid, valid with { RelativePath = "DIST/GAME.JS" }], OutputPolicy, ["application/javascript"]));
        Assert.Throws<ArgumentException>(() => DeliveryEvidenceCapabilityHandler.ValidateOutputs(
            [valid with { Size = 101 }], OutputPolicy, ["application/javascript"]));
        Assert.Throws<ArgumentException>(() => DeliveryEvidenceCapabilityHandler.ValidateOutputs(
            [valid with { Size = 80 }, Entry("dist/other.js", 80, "application/javascript")],
            OutputPolicy, ["application/javascript"]));
        Assert.Throws<ArgumentException>(() => DeliveryEvidenceCapabilityHandler.ValidateOutputs(
            [valid with { ContentType = "application/x-undeclared" }], OutputPolicy, ["application/javascript"]));
        Assert.Throws<ArgumentException>(() => DeliveryEvidenceCapabilityHandler.ValidateOutputs(
            [valid with { Sha256 = "not-a-hash" }], OutputPolicy, ["application/javascript"]));
    }

    [Fact]
    public void BuildClaimRejectsSpoofingStaleRevisionAndExpiredLease()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var handler = new DeliveryEvidenceCapabilityHandler(db, new FixedTimeProvider(now));
        var providerId = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var build = new DeliveryBuildRecord
        {
            ProviderInstallationId = providerId,
            ClaimId = claimId,
            Revision = 4,
            LeaseExpiresAt = now.AddMinutes(1)
        };

        handler.RequireActiveClaim(build, providerId, claimId, 4);
        Assert.Throws<UnauthorizedAccessException>(() =>
            handler.RequireActiveClaim(build, Guid.NewGuid(), claimId, 4));
        Assert.Throws<UnauthorizedAccessException>(() =>
            handler.RequireActiveClaim(build, providerId, Guid.NewGuid(), 4));
        Assert.Throws<DbUpdateConcurrencyException>(() =>
            handler.RequireActiveClaim(build, providerId, claimId, 3));
        build.LeaseExpiresAt = now.AddSeconds(-1);
        Assert.Throws<InvalidOperationException>(() =>
            handler.RequireActiveClaim(build, providerId, claimId, 4));
    }

    [Fact]
    public void AdapterOutputPolicyMustBeBoundedAndDenyEscapes()
    {
        const string definition = """
        {
          "key":"example.v1","version":1,"displayName":"Example",
          "requiredExecutableVersions":{"tool":"1.0"},
          "outputPolicy":{"maximumFileCount":2,"maximumFileBytes":100,"maximumTotalBytes":150,"denySymlinks":false,"denyAbsolutePaths":true},
          "supportedContentTypes":["text/plain"],"previewModes":["download"],
          "recipes":[{"key":"example.recipe.v1","operations":["build"],"targetKeys":["test"],"configurationSchema":{"type":"object"},"requiredEnvironmentProfileKeys":["office.test.v1"],"certificationFixtures":[{"key":"fixture","resource":"fixtures/test","expectedCheckKeys":["build"]}]}]
        }
        """;

        var error = Assert.Throws<ArgumentException>(() => ToolchainAdapterDefinitionValidator.Validate(
            new PluginToolchainAdapterContribution
            {
                Key = "example.v1", Version = 1, DefinitionResource = "toolchains/example.v1.json"
            }, Encoding.UTF8.GetBytes(definition)));

        Assert.Contains("deny symlinks", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizedManifestDigestIsStableAcrossOrderingAndDirectorySeparators()
    {
        var first = Entry("dist\\game.js", 64, "application/javascript");
        var second = Entry("captures/frame.png", 80, "image/png") with { TypeKey = "capture" };

        var forward = DeliveryEvidenceCapabilityHandler.ComputeNormalizedOutputManifestHash([first, second]);
        var reverse = DeliveryEvidenceCapabilityHandler.ComputeNormalizedOutputManifestHash(
            [second, first with { RelativePath = "dist/game.js" }]);

        Assert.Equal(64, forward.Length);
        Assert.Equal(forward, reverse);
    }

    [Fact]
    public async Task PrivateSourceFetchUsesOneCredentialIsolatedExactRevisionSnapshot()
    {
        var workloadId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var buildId = Guid.NewGuid();
        var commit = new string('a', 40);
        var workload = new ToolchainBuildWorkloadSpecification(
            workloadId,
            new GuestImageReference("image", "1", "sha256:" + new string('b', 64), "linux", "x64"),
            new WorkloadResourceLimits(1, 100, 512, 1024, 64, 1024, TimeSpan.FromMinutes(1)),
            new BrokerChannelLease(Guid.NewGuid(), "1.0", "token", "sha256:" + new string('b', 64),
                "sha256:" + new string('c', 64), DateTimeOffset.UtcNow.AddMinutes(1)),
            new AgentArtifactReference("sha256:" + new string('c', 64), "signature", "1.0", "linux", "x64"),
            new RuntimeAgentIdentity(installationId, Guid.NewGuid().ToString("D"), Guid.NewGuid()),
            new RepositoryDescriptor("https://github.com/example/private-game.git", commit, false, "recipe", "1"),
            buildId, 1, "recipe", "linux-x64", "{}", ["adapter"], [], 1024, 1024);
        var archive = Encoding.UTF8.GetBytes("trusted-source");
        var preparations = 0;
        var handler = new ToolchainBuildBrokerOperationHandler(
            workload,
            new RejectingBrokerHandler(),
            null!,
            _ => Task.CompletedTask,
            _ =>
            {
                preparations++;
                return Task.FromResult<ToolchainSourceArchive?>(new ToolchainSourceArchive(archive, new string('d', 64)));
            },
            NullLogger.Instance);

        var first = await handler.HandleAsync(Fetch(workloadId, installationId, commit, 0, 7), default);
        var second = await handler.HandleAsync(Fetch(workloadId, installationId, commit, 7, 20), default);

        Assert.Equal("trusted", Encoding.UTF8.GetString(first.Body.Span));
        Assert.Equal("-source", Encoding.UTF8.GetString(second.Body.Span));
        Assert.Equal("flat", first.Headers["X-CSweet-Archive-Layout"]);
        Assert.Equal("true", second.Headers["X-CSweet-Complete"]);
        Assert.Equal(1, preparations);
    }

    private static W.BuildOutputManifestEntry Entry(string path, long size, string contentType) =>
        new(path, new string('a', 64), size, contentType, "package");

    private static BrokerOperationContext Fetch(
        Guid workloadId, Guid installationId, string commit, long offset, int maximumBytes) => new(
            workloadId,
            installationId,
            Guid.NewGuid().ToString("N"),
            "build.fetch",
            "POST",
            "/build/fetch",
            new Dictionary<string, string>(),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                url = $"https://codeload.github.com/example/private-game/zip/{commit}",
                offset,
                maximumBytes
            }));

    private sealed class RejectingBrokerHandler : IAgentBrokerOperationHandler
    {
        public Task<BrokerOperationResult> HandleAsync(BrokerOperationContext request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The runtime broker was not expected for a source fetch.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
