using System.Reflection;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.UnitTests;

public sealed class NodeResourceFulfillmentTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Published_provider_install_dispatch_restart_and_real_certification_receipts_control_readiness(bool reproducible)
    {
        var name = Guid.NewGuid().ToString(); var organization = Guid.NewGuid();
        var services = new ServiceCollection(); services.AddDbContext<CSweetDbContext>(x => x.UseInMemoryDatabase(name));
        await using var container = services.BuildServiceProvider();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(name).Options);
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = NodeToolchainFulfillmentHandler.ProviderId,
            Version = "1.0.2", PublisherId = "com.csweet", PluginKind = PluginKind.Service, Status = AgentPackageVersionStatus.Built,
            PackageSource = new() { Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/CrosswiredStudios/CSweet.Plugins.Toolchain.NodeTypeScript" },
            ReleaseTag = "v1.0.2", PackageDigest = new string('c',64), ManifestDigest = "manifest", ArtifactSignature = "signature",
            ProjectPath = "src/Node.csproj", ArtifactOperatingSystem = "linux", ArtifactArchitecture = "x64", ManifestJson = """
            {"id":"com.csweet.toolchain.node-typescript","version":"1.0.2","publisher":{"id":"com.csweet","name":"C-Sweet"},
             "runtime":{"type":"dotnet-project"},"provides":[],"requires":[{"name":"platform.build.read.v2"},
             {"name":"platform.build.claim.v1"},{"name":"platform.build.heartbeat.v1"},{"name":"platform.build.report.v2"}],
             "events":{"subscribes":[]},"webAccess":{"mode":"None"},"configuration":[],"credentials":[]}
            """ };
        var definition = new ToolchainAdapterDefinitionRecord { Id = Guid.NewGuid(), Key = "node-typescript.v1", Version = 1,
            ProviderPackageId = package.AgentId, ProviderPackageVersion = package.Version, DefinitionDigest = "definition", DefinitionJson = """
            {"recipes":[
             {"key":"phaser.web-2d.v1","targetKeys":["web"],"requiredEnvironmentProfileKeys":["office.linux-x64.node-24.v1"],
              "certificationFixtures":[{"key":"phaser","resource":"fixtures/phaser","expectedCheckKeys":["build","test"]}]},
             {"key":"babylon.web-3d.v1","targetKeys":["web"],"requiredEnvironmentProfileKeys":["office.linux-x64.node-24.v1"],
              "certificationFixtures":[{"key":"babylon","resource":"fixtures/babylon","expectedCheckKeys":["build","test"]}]}],
             "outputPolicy":{"maximumTotalBytes":1024}}
            """ };
        var node = new ExecutionNode { Id = Guid.NewGuid(), Name = "Office", Status = ExecutionNodeStatus.Ready, LastHeartbeatAt = DateTimeOffset.UtcNow };
        node.Providers.Add(new() { Id = Guid.NewGuid(), ExecutionNodeId = node.Id, IsAvailable = true, SupportsToolchainBuildWorkloads = true,
            GuestImageDigest = "sha256:" + new string('a',64) });
        db.Add(new AgentRuntimeGlobalSettings { Id = Guid.NewGuid(), DefaultBuildExecutionPoolId = Guid.NewGuid(), UpdatedAt = DateTimeOffset.UtcNow });
        db.AddRange(package, definition, node, new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization,
            DisplayName = "Owner", IsActive = true, EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner });
        await db.SaveChangesAsync();
        var installCount = 0;
        var installer = DispatchProxy.Create<IPluginInstallationService, Installer>();
        ((Installer)(object)installer).Install = async (packageId, input) => {
            installCount++; Assert.Equal(organization.ToString("D"), input.BusinessId); Assert.Equal("OnDemand", input.ActivationMode);
            Assert.Equal(4, input.GrantedRequestedCapabilities.Count); Assert.Equal(package.PackageDigest, input.ExpectedPackageDigest);
            var installed = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = input.BusinessId, PackageVersionId = packageId,
                PackageVersion = package, IsEnabled = true, SetupState = PluginSetupState.Ready, RevisionStatus = PluginRevisionStatus.Active,
                Grant = new() { Id = Guid.NewGuid(), RequiredCapabilitiesJson = JsonSerializer.Serialize(input.GrantedRequestedCapabilities), ProvidedCapabilitiesJson = JsonSerializer.Serialize(input.GrantedCapabilities) } };
            db.Add(installed); await db.SaveChangesAsync();
            return JsonSerializer.Deserialize<AgentInstallationResponse>("{}")! with { Id = installed.Id };
        };
        var handler = new NodeToolchainFulfillmentHandler(db, null!, null!, null!, installer,
            new ExecutionWorkloadOrchestrator(db, TimeProvider.System), new Images(), TimeProvider.System);
        var input = new ResourceRequest(Guid.NewGuid(), ResourceCapabilities.BuildToolchain, "phaser.web-2d.v1", "Build the approved game", "build");
        var plan = await handler.PrepareAsync(organization, input, default);
        Assert.Equal(0, installCount); Assert.Empty(await db.ToolchainCertificationRuns.ToListAsync());
        var state = new ResourceRequestState(Guid.NewGuid(), input.WorkstreamId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            input.ResourceType, input.ProfileKey, input.Purpose, "Approved", 1, "hash", plan, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await handler.ValidateAsync(organization, state, default);
        var installedProgress = await handler.ReconcileAsync(organization, state, default);
        Assert.Equal("Installing", installedProgress.Status);
        // Lost installation reply: reconcile from Approved and discover the already persisted installation.
        var certifying = await handler.ReconcileAsync(organization, state, default);
        state = state with { Status = certifying.Status, ProviderInstallationId = certifying.ProviderInstallationId, CertificationRunId = certifying.CertificationRunId };
        await handler.ReconcileAsync(organization, state, default);
        Assert.Equal(1, installCount); Assert.Equal(4, await db.DeliveryBuilds.CountAsync());
        Assert.Equal(4, await db.AgentRuntimeInstances.CountAsync()); Assert.Empty(await db.ToolchainInstallationEligibilities.ToListAsync());
        foreach (var build in await db.DeliveryBuilds.ToListAsync())
        {
            build.Status = W.DeliveryBuildStatuses.Succeeded;
            var provenance = new W.BuildExecutionProvenance(package.AgentId, package.Version, definition.DefinitionDigest,
                plan.EnvironmentImageDigest, JsonSerializer.SerializeToElement(new { node = "24.15.0" }), "lock", ["npm ci --offline"], "source",
                !reproducible && build.CertificationPass == 2 ? "different" : build.RecipeKey + "-manifest", []);
            build.ProvenanceJson = JsonSerializer.Serialize(provenance, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        await db.SaveChangesAsync();
        var worker = new ToolchainCertificationWorker(container.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<ToolchainCertificationWorker>.Instance);
        await worker.ReviewAsync(default);
        await db.Entry(await db.ToolchainCertificationRuns.SingleAsync()).ReloadAsync();
        var result = await handler.ReconcileAsync(organization, state, default);
        Assert.Equal(reproducible ? "Ready" : "Failed", result.Status);
        Assert.Equal(reproducible ? 1 : 0, await db.ToolchainInstallationEligibilities.CountAsync());
        package.PackageDigest = new string('d',64); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ValidateAsync(organization, state, default));
    }

    [Fact]
    public void Published_service_bundle_selection_preserves_platform_and_checksum_binding()
    {
        var release = new GitHubReleaseInfo("v1.0.2", false, false, [
            new("Node-1.0.2-linux-arm64.cspb", "https://github.com/arm", 1),
            new("Node-1.0.2-linux-x64.cspb", "https://github.com/x64", 1),
            new("Node-1.0.2-linux-x64.cspb.sha256", "https://github.com/hash", 1)]);
        var bundle = PrebuiltBundleAssets.SelectBundle(release)!;
        Assert.EndsWith("linux-x64.cspb", bundle.Name);
        Assert.NotNull(PrebuiltBundleAssets.FindChecksumSidecar(release, bundle));
        Assert.True(PrebuiltBundleAssets.IsChecksumSidecar(bundle.Name + ".sha256"));
    }
    public class Installer : DispatchProxy
    {
        public Func<Guid, InstallAgentRequest, Task<AgentInstallationResponse>> Install = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name == "InstallAsync"
            ? Install((Guid)arguments![0]!, (InstallAgentRequest)arguments[1]!) : throw new NotSupportedException(method?.Name);
    }
    private sealed class Images : IGuestImageRegistry
    {
        public Task<GuestImageReference> ResolveAsync(GuestImageResolutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GuestImageReference(request.LogicalImageId, "1.0", request.ExpectedDigest!, request.OperatingSystem, request.Architecture));
    }
}
