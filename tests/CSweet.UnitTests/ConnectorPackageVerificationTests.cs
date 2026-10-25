using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using F = CSweet.UnitTests.PluginAccountFlowTests.Fixture;

namespace CSweet.UnitTests;

public sealed class ConnectorPackageVerificationTests
{
    [Fact]
    public async Task ApprovedConnectorBecomesConnectableWithoutFleetRuntimeSettingsOrExecutingCode()
    {
        await using var f = await F.Create();
        await PrepareReviewedPackage(f);
        var fleet = DispatchProxy.Create<IExecutionFleetService, UnavailableFleet>();
        var worker = Worker(f, fleet);
        var jobId = await worker.QueueAsync(f.Inner.Connector.PackageVersionId);
        Assert.False((await f.Service.GetAsync(f.Admin, f.Id, default)).CanConnect);

        Assert.True(await worker.ProcessNextAsync());

        var package = f.Inner.Connector.PackageVersion!;
        var job = await f.Inner.Db.AgentBuildJobs.SingleAsync(x => x.Id == jobId);
        Assert.Equal(AgentBuildStatus.Succeeded, job.Status);
        Assert.Equal(AgentPackageVersionStatus.Built, package.Status);
        Assert.Equal(AgentBuildService.ConnectorArtifactFormat, package.ArtifactFormatVersion);
        Assert.Equal(64, package.PackageDigest!.Length);
        Assert.Null(package.PackagePath);
        Assert.Null(package.ArtifactSignature);
        Assert.Null(job.ExecutionPoolId);
        Assert.DoesNotContain("VM", job.StepsJson.Replace("no VM", "", StringComparison.Ordinal));
        Assert.DoesNotContain("Compile", job.StepsJson);
        Assert.Equal(0, ((UnavailableFleet)(object)fleet).Calls);
        Assert.Empty(await f.Inner.Db.AgentRuntimeInstances.ToArrayAsync());
        Assert.True((await f.Service.GetAsync(f.Admin, f.Id, default)).CanConnect);

        // Actual OAuth service, not a fabricated authorization URL or pre-marked Built fixture.
        var begin = await f.Begin();
        Assert.StartsWith("https://identity.example.com/authorize?", begin.AuthorizationUrl);
        await f.Complete(begin);
        await f.Service.ImportAsync(begin.SetupId, default);
        Assert.Equal(2, Assert.Single((await f.Service.GetAsync(f.Admin, f.Id, default)).Accounts).Resources.Count);
        Assert.Empty(await f.Inner.Db.PluginAccountBusinessGrants.ToArrayAsync());
    }

    [Fact]
    public async Task ActualYouTubeSourceInstallVerifiesWithoutVmAndReachesProviderConfiguration()
    {
        await using var f = await F.Create();
        using var resource = typeof(ConnectorPackageVerificationTests).Assembly.GetManifestResourceStream("YouTubeConnector.0.2.0.json")!;
        await PrepareReviewedPackage(f, await new StreamReader(resource).ReadToEndAsync());
        var package = f.Inner.Connector.PackageVersion!;
        package.Status = AgentPackageVersionStatus.Previewed;
        f.Inner.Connector.RevisionStatus = PluginRevisionStatus.Retired;
        await f.Inner.Db.SaveChangesAsync();
        var manifest = JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, Json)!;
        var installed = await AgentInstallationServiceTests.CreatePluginInstallerForAcceptance(f.Inner.Db)
            .InstallAsync(package.Id, new("enterprise", "", 0, "", manifest.Provides.Select(c => c.Name).ToArray(),
                [], [], [], [], 0, 0, 0) { PluginScope = "System" });
        Assert.Null(installed.Schedule);
        Assert.Equal(0, installed.MemoryMb);
        Assert.True(await Worker(f, DispatchProxy.Create<IExecutionFleetService, UnavailableFleet>()).ProcessNextAsync());
        Assert.Equal(AgentPackageVersionStatus.Built, package.Status);
        Assert.Equal(AgentBuildService.ConnectorArtifactFormat, package.ArtifactFormatVersion);
        Assert.Empty(await f.Inner.Db.AgentRuntimeInstances.ToArrayAsync());
        Assert.Empty(await f.Inner.Db.AgentSchedules.ToArrayAsync());
        // The fixture has an Example provider, not Google. Verification must not bypass
        // deployment client setup or send a user to a mismatching OAuth application.
        var view = await f.Service.GetAsync(f.Admin, installed.Id, default);
        Assert.False(view.CanConnect);
        Assert.Equal("provider_configuration_mismatch", view.ReadinessMessage);
    }

    [Fact]
    public async Task WaitingExecutableDoesNotBlockConnectorVerification()
    {
        await using var f = await F.Create();
        await PrepareReviewedPackage(f);
        f.Inner.Db.AgentBuildJobs.Add(new() { Id = Guid.NewGuid(), PackageVersionId = f.Inner.Requester.PackageVersionId,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await f.Inner.Db.SaveChangesAsync();
        var fleet = DispatchProxy.Create<IExecutionFleetService, UnavailableFleet>();
        var worker = Worker(f, fleet);
        await worker.QueueAsync(f.Inner.Connector.PackageVersionId);
        Assert.True(await worker.ProcessNextAsync());
        Assert.False(await worker.ProcessNextAsync());
        Assert.Equal(AgentBuildStatus.Queued, (await f.Inner.Db.AgentBuildJobs.SingleAsync(x =>
            x.PackageVersionId == f.Inner.Requester.PackageVersionId)).Status);
        Assert.Equal(1, ((UnavailableFleet)(object)fleet).Calls);
    }

    [Theory]
    [InlineData("changed-manifest")]
    [InlineData("wrong-identity")]
    [InlineData("wrong-capabilities")]
    [InlineData("mutable-commit")]
    [InlineData("unsafe-destination")]
    [InlineData("executable-network")]
    public async Task InvalidOrChangedDeclarationsFailClosedWithoutBuildFallback(string change)
    {
        await using var f = await F.Create();
        await PrepareReviewedPackage(f);
        var package = f.Inner.Connector.PackageVersion!;
        switch (change)
        {
            case "changed-manifest": package.ManifestJson += " "; break;
            case "wrong-identity": package.PublisherId = "com.impersonator"; break;
            case "wrong-capabilities": package.CapabilityDescriptorsDigest = new string('0', 64); break;
            case "mutable-commit": package.CommitSha = "main"; break;
            case "unsafe-destination":
            case "executable-network":
                var manifest = JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, Json)!;
                manifest = change == "unsafe-destination"
                    ? manifest with { ProviderOperations = manifest.ProviderOperations.Select(o => o with {
                        Http = o.Http! with { Endpoint = "https://unreviewed.example.net/data" } }).ToArray() }
                    : manifest with { WebAccess = new() { Mode = PluginWebAccessMode.AllPublic, Purpose = "Unsafe" } };
                package.ManifestJson = JsonSerializer.Serialize(manifest, Json);
                package.ManifestDigest = Digest(package.ManifestJson);
                break;
        }
        await f.Inner.Db.SaveChangesAsync();
        var worker = Worker(f);
        await worker.QueueAsync(package.Id);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(AgentPackageVersionStatus.Failed, package.Status);
        Assert.Null(package.PackageDigest);
        var job = await f.Inner.Db.AgentBuildJobs.SingleAsync();
        Assert.Equal(AgentBuildStatus.Failed, job.Status);
        Assert.NotEmpty(job.FailureMessage!);
        if (change is "unsafe-destination" or "executable-network")
            await Assert.ThrowsAsync<JsonException>(() => f.Service.GetAsync(f.Admin, f.Id, default));
        else
            Assert.False((await f.Service.GetAsync(f.Admin, f.Id, default)).CanConnect);
        Assert.Single(await f.Inner.Db.AgentBuildJobs.ToArrayAsync());
    }

    [Fact]
    public async Task RevokedQueuedConnectorIsNotReapprovedByVerification()
    {
        await using var f = await F.Create();
        await PrepareReviewedPackage(f);
        var worker = Worker(f);
        await worker.QueueAsync(f.Inner.Connector.PackageVersionId);
        f.Inner.Connector.PackageVersion!.Status = AgentPackageVersionStatus.Revoked;
        await f.Inner.Db.SaveChangesAsync();
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(AgentPackageVersionStatus.Revoked, f.Inner.Connector.PackageVersion.Status);
        Assert.Equal(AgentBuildStatus.Cancelled, (await f.Inner.Db.AgentBuildJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task VerificationIsDeterministicAndBindsRepositoryCommitAndManifest()
    {
        await using var f = await F.Create();
        await PrepareReviewedPackage(f);
        var package = f.Inner.Connector.PackageVersion!;
        var digest = AgentBuildService.VerifyConnectorDeclarations(package);
        Assert.Equal(digest, AgentBuildService.VerifyConnectorDeclarations(package));
        package.CommitSha = new string('b', 40);
        Assert.NotEqual(digest, AgentBuildService.VerifyConnectorDeclarations(package));
        package.CommitSha = new string('a', 40);
        package.PackageSource!.RepositoryUrl = "https://github.com/other/platform-connector";
        Assert.NotEqual(digest, AgentBuildService.VerifyConnectorDeclarations(package));
        package.PackageSource.RepositoryUrl = "https://github.com/example/platform-connector";
        package.ManifestJson += " "; package.ManifestDigest = Digest(package.ManifestJson);
        Assert.NotEqual(digest, AgentBuildService.VerifyConnectorDeclarations(package));
    }

    [Fact]
    public async Task InterruptedVerificationRecoversWithoutAnExecutionFleet()
    {
        await using var f = await F.Create();
        await PrepareReviewedPackage(f);
        var worker = Worker(f, DispatchProxy.Create<IExecutionFleetService, UnavailableFleet>());
        await worker.QueueAsync(f.Inner.Connector.PackageVersionId);
        var old = await f.Inner.Db.AgentBuildJobs.SingleAsync();
        old.TransitionTo(AgentBuildStatus.Cloning, DateTimeOffset.UtcNow);
        await f.Inner.Db.SaveChangesAsync();
        Assert.Equal(1, await worker.RecoverInterruptedAsync());
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(AgentBuildStatus.Cancelled, old.Status);
        Assert.Equal(AgentBuildStatus.Succeeded, (await f.Inner.Db.AgentBuildJobs.SingleAsync(x => x.Attempt == 2)).Status);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static async Task PrepareReviewedPackage(F f, string? manifestJson = null)
    {
        var p = f.Inner.Connector.PackageVersion!;
        var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson ?? p.ManifestJson, Json)!;
        if (manifestJson is null)
            manifest = manifest with { ManifestVersion = "2.0", Publisher = new() { Id = "com.example", Name = "Example" },
                Runtime = manifest.Runtime with { Type = "dotnet-project", ProjectPath = "src/Connector/Connector.csproj", TargetFramework = "net10.0" },
                Provides = manifest.Provides.Select(c => c with { Description = "Read the declared provider resource." }).ToArray(),
                Connections = manifest.Connections.Select(c => c with { ScopeSets = c.ScopeSets.Select(s => s with { Purpose = "Read account information." }).ToArray() }).ToArray(),
                Setup = manifest.Setup! with { Flows = manifest.Setup.Flows.Select(flow => flow with { Title = "Connect account",
                    Steps = flow.Steps.Select(s => s with { Title = "Review account access" }).ToArray() }).ToArray() } };
        p.ManifestJson = manifestJson ?? JsonSerializer.Serialize(manifest, Json);
        p.ManifestDigest = Digest(p.ManifestJson);
        p.CapabilityDescriptorsDigest = AgentImportPreviewService.CapabilityDescriptorsDigest(manifest);
        p.AgentId = manifest.Id; p.AgentName = manifest.Name; p.Version = manifest.Version;
        p.PublisherId = manifest.Publisher.Id; p.PublisherName = manifest.Publisher.Name;
        p.RuntimeType = manifest.Runtime.Type; p.ProjectPath = manifest.Runtime.ProjectPath; p.TargetFramework = manifest.Runtime.TargetFramework;
        p.CommitSha = new string('a', 40); p.Status = AgentPackageVersionStatus.Approved;
        p.PackageDigest = null; p.PackagePath = null; p.ArtifactSignature = null;
        p.PackageSource = new() { Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/example/platform-connector",
            RepositoryOwner = "example", RepositoryName = "platform-connector", DefaultBranch = "main" };
        p.PackageSourceId = p.PackageSource.Id;
        f.Inner.Db.AgentPackageSources.Add(p.PackageSource);
        await f.Inner.Db.SaveChangesAsync();
    }

    internal static AgentBuildService Worker(F f, IExecutionFleetService? fleet = null) => new(f.Inner.Db,
        new ForbiddenBuildExecutor(), new TestAuditEventWriter(), NullLogger<AgentBuildService>.Instance, fleet);

    public class UnavailableFleet : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IExecutionFleetService.IsReadyAsync), method!.Name);
            Calls++;
            return Task.FromResult(false);
        }
    }

    private sealed class ForbiddenBuildExecutor : IAgentBuildExecutor
    {
        public Task<AgentBuildWorkspace> CloneAsync(AgentBuildExecutionRequest r, IAgentBuildProgressReporter p, CancellationToken ct = default) => throw new InvalidOperationException("Connector code must not be cloned or executed.");
        public Task<AgentBuildExecutionResult> BuildAsync(AgentBuildExecutionRequest r, AgentBuildWorkspace w, IAgentBuildProgressReporter p, CancellationToken ct = default) => throw new InvalidOperationException("Connector code must not be built.");
        public Task CleanupWorkspaceAsync(AgentBuildWorkspace w, CancellationToken ct = default) => throw new InvalidOperationException("No connector workspace should exist.");
    }
}
