using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Setup;
using CSweet.Office.Contracts.Workloads;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.Core;

/// <summary>Downloads published artifacts, uses normal installation, and proves exact Office build capacity.</summary>
public sealed class NodeToolchainFulfillmentHandler(CSweetDbContext db, IGitHubAgentRepositoryClient repository,
    IAgentImportPreviewService previews, PrebuiltBundleInstallService bundles, IPluginInstallationService installations,
    IExecutionWorkloadOrchestrator workloads, IGuestImageRegistry images, TimeProvider clock) : IResourceFulfillmentHandler
{
    public const string ProviderId = "com.csweet.toolchain.node-typescript";
    public string Key => "node-typescript.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Supports(string type, string profile) => type == ResourceCapabilities.BuildToolchain && profile is "phaser.web-2d.v1" or "babylon.web-3d.v1";

    public async Task<ResourcePlan> PrepareAsync(Guid organization, ResourceRequest request, CancellationToken token)
    {
        // This handler's trusted source is operator-owned code, never a model-supplied repository or URL.
        var package = await db.AgentPackageVersions.Include(x => x.PackageSource).Where(x => x.AgentId == ProviderId &&
            x.PublisherId == "com.csweet" && x.PluginKind == PluginKind.Service && x.Status == AgentPackageVersionStatus.Built &&
            x.PackageSource != null && x.PackageSource.RepositoryUrl == "https://github.com/CrosswiredStudios/CSweet.Plugins.Toolchain.NodeTypeScript" && x.ReleaseTag != null)
            .OrderByDescending(x => x.ImportedAt).FirstOrDefaultAsync(token);
        if (package is null)
        {
            const string owner = "CrosswiredStudios"; const string name = "CSweet.Plugins.Toolchain.NodeTypeScript";
            var release = await repository.GetLatestReleaseAsync(owner, name, token);
            if (release is null)
                throw new ResourcePreparationDependencyException("resource.provider_release_unavailable",
                    "C-Sweet cannot access the published Node/TypeScript release. The repository may be private or have no stable release. Configure GitHubAgentRepository:AccessToken with repository read access for both API and AgentHost, or publish an accessible provider release. Setup will retry; no installation is approved.");
            if (release.Draft || release.Prerelease || PrebuiltBundleAssets.SelectBundle(release) is null)
                throw new ResourcePreparationDependencyException("resource.provider_bundle_unavailable",
                    "The stable Node/TypeScript release needs a compatible linux-x64 .cspb or .csab bundle and checksum. Publish or repair those assets; setup will retry without creating an execution grant.");
            var preview = await previews.PreviewAsync(new($"https://github.com/{owner}/{name}", release.TagName), token);
            package = await db.AgentPackageVersions.Include(x => x.PackageSource).SingleAsync(x => x.Id == preview.ImportId, token);
            if (package.AgentId != ProviderId || package.PublisherId != "com.csweet" || package.PluginKind != PluginKind.Service || package.ReleaseTag != release.TagName)
                throw new InvalidOperationException("The published provider does not match the trusted resource profile.");
            // Stage and validate the exact published bytes before approval; this grants no runtime authority.
            await bundles.InstallAsync(package.Id, token);
            await db.Entry(package).ReloadAsync(token);
        }
        var definition = await db.ToolchainAdapterDefinitions.SingleAsync(x => x.ProviderPackageId == ProviderId &&
            x.ProviderPackageVersion == package.Version && x.Key == Key, token);
        using var json = JsonDocument.Parse(definition.DefinitionJson);
        var recipe = json.RootElement.GetProperty("recipes").EnumerateArray().Single(x => x.GetProperty("key").GetString() == request.ProfileKey);
        var profiles = recipe.GetProperty("requiredEnvironmentProfileKeys").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var now = clock.GetUtcNow();
        var digests = await db.ExecutionNodeProviders.AsNoTracking().Where(x => x.IsAvailable && x.SupportsToolchainBuildWorkloads &&
            x.ExecutionNode != null && x.ExecutionNode.Status == ExecutionNodeStatus.Ready && x.ExecutionNode.LastHeartbeatAt >= now.AddMinutes(-2))
            .Select(x => x.GuestImageDigest).Distinct().ToListAsync(token);
        foreach (var profile in profiles.Where(x => x.Contains("linux-x64", StringComparison.Ordinal)))
        foreach (var digest in digests.Order(StringComparer.Ordinal))
        {
            try
            {
                await images.ResolveAsync(new(profile, null, "linux", "x64", AgentTrustLevel.OrganizationApproved, "1.0", ExpectedDigest: digest), token);
                var manifest = Manifest(package);
                ValidateManifest(manifest);
                return new(Key, "Prepare Node/TypeScript build and browser testing for this project.", package.Id,
                    package.AgentId, package.Version, package.ManifestDigest, package.PackageDigest!, definition.Id, definition.DefinitionDigest,
                    profile, digest, manifest.Provides.Select(x => x.Name).ToArray(), manifest.Events.Subscribes,
                    manifest.Requires.Select(x => x.Name).ToArray(), 0, "USD");
            }
            catch (InvalidOperationException) { }
        }
        throw new ResourcePreparationDependencyException("resource.office_image_unavailable", "No compatible managed Office image is ready. Complete host setup; a generic Node compute image is not a substitute for this provider profile. Setup will retry after host capacity becomes available.");
    }

    public async Task ValidateAsync(Guid organization, ResourceRequestState state, CancellationToken token)
    {
        var plan = state.Plan ?? throw new InvalidOperationException("Prepare an exact resource plan before validation.");
        var package = await db.AgentPackageVersions.AsNoTracking().SingleAsync(x => x.Id == plan.PackageVersionId, token);
        var definition = await db.ToolchainAdapterDefinitions.AsNoTracking().SingleAsync(x => x.Id == plan.DefinitionId, token);
        if (!Supports(state.ResourceType, state.ProfileKey) || package.AgentId != ProviderId || package.PublisherId != "com.csweet" ||
            package.PluginKind != PluginKind.Service || package.Status != AgentPackageVersionStatus.Built ||
            package.Version != plan.PackageVersion || package.ManifestDigest != plan.ManifestDigest || package.PackageDigest != plan.PackageDigest ||
            definition.DefinitionDigest != plan.DefinitionDigest || definition.ProviderPackageVersion != package.Version ||
            definition.ProviderPackageId != package.AgentId)
            throw new InvalidOperationException("The reviewed provider artifact or definition changed; a fresh plan and approval are required.");
        var manifest = Manifest(package); ValidateManifest(manifest);
        if (!manifest.Requires.Select(x => x.Name).Order().SequenceEqual(plan.GrantedRequestedCapabilities.Order()) ||
            !manifest.Provides.Select(x => x.Name).Order().SequenceEqual(plan.GrantedCapabilities.Order()) ||
            !manifest.Events.Subscribes.Order().SequenceEqual(plan.GrantedSubscriptions.Order()))
            throw new InvalidOperationException("The reviewed provider permissions changed.");
        await images.ResolveAsync(new(plan.EnvironmentProfileKey, null, "linux", "x64", AgentTrustLevel.OrganizationApproved,
            "1.0", ExpectedDigest: plan.EnvironmentImageDigest), token);
    }

    public async Task<ResourceFulfillmentProgress> ReconcileAsync(Guid organization, ResourceRequestState state, CancellationToken token)
    {
        var plan = state.Plan ?? throw new InvalidOperationException("Prepare and approve an exact resource plan before installation.");
        var provider = state.ProviderInstallationId is { } id
            ? await db.AgentInstallations.Include(x => x.PackageVersion).Include(x => x.Grant).SingleOrDefaultAsync(x => x.Id == id && x.BusinessId == organization.ToString("D"), token)
            : await db.AgentInstallations.Include(x => x.PackageVersion).Include(x => x.Grant).Where(x => x.BusinessId == organization.ToString("D") &&
                x.PackageVersionId == plan.PackageVersionId && x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(token);
        if (provider is null)
        {
            if (state.ProviderInstallationId.HasValue) throw new InvalidOperationException("The approved provider installation was removed. Do not silently recreate revoked access.");
            var installed = await installations.InstallAsync(plan.PackageVersionId, new(organization.ToString("D"), "OnDemand", 300,
                "Skip", plan.GrantedCapabilities, plan.GrantedSubscriptions, [], [], [], 1800, 1024, 100)
                { GrantedRequestedCapabilities = plan.GrantedRequestedCapabilities, ExpectedPackageDigest = plan.PackageDigest }, token);
            return new("Installing", "Provider installed; checking exact installation readiness.", installed.Id);
        }
        if (!provider.IsEnabled || provider.RevisionStatus != PluginRevisionStatus.Active || provider.PackageVersionId != plan.PackageVersionId)
            throw new UnauthorizedAccessException("The approved provider installation changed or was disabled.");
        var required = JsonSerializer.Deserialize<string[]>(provider.Grant?.RequiredCapabilitiesJson ?? "[]", Json) ?? [];
        var provided = JsonSerializer.Deserialize<string[]>(provider.Grant?.ProvidedCapabilitiesJson ?? "[]", Json) ?? [];
        if (!required.Order().SequenceEqual(plan.GrantedRequestedCapabilities.Order()) || !provided.Order().SequenceEqual(plan.GrantedCapabilities.Order()))
            throw new UnauthorizedAccessException("The approved provider permissions were changed or revoked.");
        if (provider.SetupState != PluginSetupState.Ready) return new("Installing", "Provider setup is still running.", provider.Id);
        var now = clock.GetUtcNow();
        var eligible = await db.ToolchainInstallationEligibilities.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organization &&
            x.ProviderInstallationId == provider.Id && x.ToolchainDefinitionId == plan.DefinitionId && x.EnvironmentImageDigest == plan.EnvironmentImageDigest &&
            x.EnvironmentProfileKey == plan.EnvironmentProfileKey && x.RevokedAt == null && x.ExpiresAt > now, token);
        if (eligible is not null)
        {
            var verified = await db.ToolchainCertificationRuns.AsNoTracking().SingleAsync(x => x.Id == eligible.CertificationRunId, token);
            if (verified.Status == W.ToolchainCertificationStatuses.Certified && verified.ProviderPackageDigest == Normalize(plan.PackageDigest) &&
                verified.DefinitionDigest == plan.DefinitionDigest)
                return new("Ready", "Exact provider/image fixtures passed and current certified capacity is available.", provider.Id, verified.Id);
        }
        if (state.Status == "Ready") return new("NeedsAttention", "Previously verified certification expired or was revoked.", provider.Id, state.CertificationRunId);
        var definition = await db.ToolchainAdapterDefinitions.SingleAsync(x => x.Id == plan.DefinitionId, token);
        var run = await ToolchainCertificationScheduler.StartAsync(new(organization, plan.DefinitionId, provider.Id,
            plan.EnvironmentProfileKey, plan.EnvironmentImageDigest), definition, provider, workloads, images, db, CertificationIdentity(state), token);
        if (run.Status is W.ToolchainCertificationStatuses.Failed or W.ToolchainCertificationStatuses.Revoked or W.ToolchainCertificationStatuses.Expired)
            return new("Failed", run.RevocationReason ?? "Certification did not pass.", provider.Id, run.Id);
        return new("Certifying", "Running the real reproducible build/browser fixtures.", provider.Id, run.Id);
    }
    private static Guid CertificationIdentity(ResourceRequestState state) => state.FulfillmentAttempt == 1 ? state.Id :
        new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{state.Id:N}:attempt:{state.FulfillmentAttempt}")).AsSpan(0, 16));
    private static PluginManifest Manifest(AgentPackageVersion package) => JsonSerializer.Deserialize<PluginManifest>(package.ManifestJson, Json)!;
    private static string Normalize(string digest) => digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest : "sha256:" + digest;
    private static void ValidateManifest(PluginManifest manifest)
    {
        var expected = new[] { "platform.build.read.v2", "platform.build.claim.v1", "platform.build.heartbeat.v1", "platform.build.report.v2" };
        if (!manifest.Requires.Select(x => x.Name).Order().SequenceEqual(expected.Order()) || manifest.WebAccess.Mode != PluginWebAccessMode.None ||
            manifest.Credentials.Count != 0 || manifest.Configuration.Count != 0 || manifest.Setup?.Required == true || manifest.Accounts is not null)
            throw new InvalidOperationException("This provider requests authority outside the supported reviewed toolchain profile.");
    }
}
