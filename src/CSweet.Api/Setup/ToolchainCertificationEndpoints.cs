using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using CSweet.Office.Contracts.Workloads;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.Api.Setup;

public static class ToolchainCertificationEndpoints
{
    public static IEndpointRouteBuilder MapToolchainCertificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/toolchain-certifications")
            .RequireAuthorization("HostAdministration");
        group.MapGet("", ListAsync);
        group.MapGet("/setup", SetupAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("", StartAsync);
        group.MapPost("/{id:guid}/revoke", RevokeAsync);
        group.MapPost("/{id:guid}/resume", ResumeAsync);
        return endpoints;
    }

    private static async Task<IResult> SetupAsync(Guid organizationId, CSweetDbContext db, CancellationToken token) =>
        organizationId == Guid.Empty ? Results.BadRequest(new { error = "organization_required" })
            : Results.Ok(await ReadSetupAsync(organizationId, db, token));

    internal static async Task<ToolchainCertificationSetup> ReadSetupAsync(Guid organizationId, CSweetDbContext db, CancellationToken token)
    {
        var installations = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Where(x =>
            x.BusinessId == organizationId.ToString() && x.Scope == PluginInstallationScope.Organization &&
            x.IsEnabled && x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active &&
            x.PackageVersion!.Status == AgentPackageVersionStatus.Built && x.PackageVersion.PluginKind == PluginKind.Service)
            .OrderBy(x => x.Id).Take(100).ToListAsync(token);
        var ids = installations.Select(x => x.PackageVersion!.AgentId).Distinct().ToList();
        var definitions = await db.ToolchainAdapterDefinitions.AsNoTracking().Where(x => ids.Contains(x.ProviderPackageId))
            .OrderBy(x => x.Id).Take(250).ToListAsync(token);
        var providers = new List<ToolchainCertificationProvider>();
        foreach (var installation in installations)
        foreach (var definition in definitions.Where(x => x.ProviderPackageId == installation.PackageVersion!.AgentId &&
                     x.ProviderPackageVersion == installation.PackageVersion.Version))
        {
            using var json = JsonDocument.Parse(definition.DefinitionJson);
            var profiles = json.RootElement.GetProperty("recipes").EnumerateArray()
                .SelectMany(x => x.GetProperty("requiredEnvironmentProfileKeys").EnumerateArray().Select(y => y.GetString()!))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            providers.Add(new(definition.Id, installation.Id, definition.DisplayName, definition.ProviderPackageVersion, profiles));
        }
        var now = DateTimeOffset.UtcNow;
        var nodes = await db.ExecutionNodes.AsNoTracking().Include(x => x.Providers).Where(x => x.Status == ExecutionNodeStatus.Ready &&
            x.LastHeartbeatAt >= now.AddMinutes(-2)).OrderBy(x => x.Id).Take(100).ToListAsync(token);
        var images = nodes.SelectMany(node => node.Providers.Where(provider => provider.IsAvailable &&
            provider.SupportsToolchainBuildWorkloads && !string.IsNullOrWhiteSpace(provider.GuestImageDigest))
            .Select(provider => new ToolchainCertificationImage(node.Name, provider.GuestImageDigest)))
            .DistinctBy(x => x.Digest).ToArray();
        return new(providers, images);
    }

    private static async Task<IResult> ListAsync(Guid? organizationId, CSweetDbContext db, CancellationToken token)
    {
        var query = db.ToolchainCertificationRuns.AsNoTracking();
        if (organizationId.HasValue) query = query.Where(x => x.OrganizationId == organizationId);
        var runs = await query.OrderByDescending(x => x.CreatedAt).Take(250).ToListAsync(token);
        var ids = runs.Select(x => x.Id).ToList();
        var counts = await db.DeliveryBuilds.AsNoTracking().Where(x => x.CertificationRunId.HasValue && ids.Contains(x.CertificationRunId.Value))
            .GroupBy(x => x.CertificationRunId!.Value)
            .Select(x => new { Id = x.Key, Total = x.Count(), Complete = x.Count(build => build.Status == W.DeliveryBuildStatuses.Succeeded) })
            .ToDictionaryAsync(x => x.Id, token);
        return Results.Ok(runs.Select(run => Map(run, counts.GetValueOrDefault(run.Id))).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, CSweetDbContext db, CancellationToken token)
    {
        var run = await db.ToolchainCertificationRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (run is null) return Results.NotFound();
        var builds = await db.DeliveryBuilds.AsNoTracking().Where(x => x.CertificationRunId == id).ToListAsync(token);
        return Results.Ok(Map(run, new { Total = builds.Count, Complete = builds.Count(x => x.Status == W.DeliveryBuildStatuses.Succeeded) }));
    }

    private static async Task<IResult> StartAsync(
        StartToolchainCertificationRequest request,
        CSweetDbContext db,
        IExecutionWorkloadOrchestrator workloads,
        IGuestImageRegistry guestImages,
        CancellationToken token)
    {
        if (request.OrganizationId == Guid.Empty || request.ValidForDays is < 1 or > 365 ||
            string.IsNullOrWhiteSpace(request.EnvironmentProfileKey) || string.IsNullOrWhiteSpace(request.EnvironmentImageDigest))
            return Results.BadRequest(new { error = "invalid_certification_request" });
        var definition = await db.ToolchainAdapterDefinitions.SingleOrDefaultAsync(x => x.Id == request.ToolchainDefinitionId, token);
        var installation = await db.AgentInstallations.Include(x => x.PackageVersion).SingleOrDefaultAsync(x =>
            x.Id == request.ProviderInstallationId && x.BusinessId == request.OrganizationId.ToString() &&
            x.Scope == PluginInstallationScope.Organization && x.IsEnabled &&
            x.SetupState == PluginSetupState.Ready && x.RevisionStatus == PluginRevisionStatus.Active, token);
        if (definition is null || installation?.PackageVersion is null) return Results.NotFound();
        if (installation.PackageVersion.AgentId != definition.ProviderPackageId || installation.PackageVersion.Version != definition.ProviderPackageVersion)
            return Results.BadRequest(new { error = "provider_package_mismatch" });
        var capacity = await db.ExecutionNodes.AsNoTracking().AnyAsync(node => node.Status == ExecutionNodeStatus.Ready &&
            node.LastHeartbeatAt >= DateTimeOffset.UtcNow.AddMinutes(-2) && node.Providers.Any(provider =>
                provider.IsAvailable && provider.SupportsToolchainBuildWorkloads && provider.GuestImageDigest == request.EnvironmentImageDigest), token);
        if (!capacity) return Results.Conflict(new { error = "certified_office_capacity_unavailable" });
        using var document = JsonDocument.Parse(definition.DefinitionJson);
        var recipes = document.RootElement.GetProperty("recipes").EnumerateArray().ToList();
        if (!recipes.Any(recipe => recipe.GetProperty("requiredEnvironmentProfileKeys").EnumerateArray()
                .Any(key => key.GetString() == request.EnvironmentProfileKey)))
            return Results.BadRequest(new { error = "environment_profile_not_declared" });
        var existing = await db.ToolchainCertificationRuns.AsNoTracking().FirstOrDefaultAsync(x =>
            x.OrganizationId == request.OrganizationId && x.ToolchainDefinitionId == definition.Id &&
            x.ProviderInstallationId == installation.Id && x.EnvironmentProfileKey == request.EnvironmentProfileKey &&
            x.EnvironmentImageDigest == request.EnvironmentImageDigest &&
            (x.Status == W.ToolchainCertificationStatuses.Pending || x.Status == W.ToolchainCertificationStatuses.Running), token);
        if (existing is not null) return Results.Conflict(new { error = "certification_already_running", existing.Id });
        var owner = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == request.OrganizationId && x.IsActive)
            .OrderByDescending(x => x.PermissionLevel).FirstOrDefaultAsync(token);
        if (owner is null) return Results.Conflict(new { error = "organization_has_no_active_owner" });
        var now = DateTimeOffset.UtcNow;
        var workstream = await db.Workstreams.SingleOrDefaultAsync(x => x.OrganizationId == request.OrganizationId &&
            x.ProfileKey == WorkstreamPurpose.ToolchainCertificationProfile, token);
        if (workstream is null)
        {
            workstream = new Workstream { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
                AccountableManagerOrganizationUserId = owner.Id, Name = "Toolchain certification",
                Outcome = "Certify immutable toolchain definitions on clean Office runtime images.",
                LifecycleStage = "Active", ManagerTitle = "Platform owner", Status = WorkstreamStatus.Active,
                ProfileKey = WorkstreamPurpose.ToolchainCertificationProfile, ProfileVersion = 1,
                ProfileDataJson = "{}", CreatedAt = now, UpdatedAt = now };
            db.Workstreams.Add(workstream);
        }
        var run = new ToolchainCertificationRunRecord { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
            ToolchainDefinitionId = definition.Id, ProviderInstallationId = installation.Id,
            EnvironmentProfileKey = request.EnvironmentProfileKey, EnvironmentImageDigest = request.EnvironmentImageDigest,
            ProviderPackageDigest = NormalizeDigest(installation.PackageVersion.PackageDigest ??
                throw new InvalidOperationException("Certification requires a built provider artifact.")),
            DefinitionDigest = definition.DefinitionDigest,
            Status = W.ToolchainCertificationStatuses.Running, CreatedAt = now, ExpiresAt = now.AddDays(request.ValidForDays) };
        db.ToolchainCertificationRuns.Add(run);
        var scheduledBuilds = new List<DeliveryBuildRecord>();
        foreach (var recipe in recipes.Where(recipe => recipe.GetProperty("requiredEnvironmentProfileKeys").EnumerateArray()
                     .Any(key => key.GetString() == request.EnvironmentProfileKey)))
        {
            var recipeKey = recipe.GetProperty("key").GetString()!;
            foreach (var target in recipe.GetProperty("targetKeys").EnumerateArray().Select(x => x.GetString()!))
            foreach (var fixture in recipe.GetProperty("certificationFixtures").EnumerateArray())
            foreach (var pass in new[] { 1, 2 })
            {
                var fixtureKey = fixture.GetProperty("key").GetString()!;
                var fixtureResource = fixture.GetProperty("resource").GetString()!;
                var build = new DeliveryBuildRecord { Id = Guid.NewGuid(), OrganizationId = request.OrganizationId,
                    WorkstreamId = workstream.Id, ToolchainDefinitionId = definition.Id, ProviderInstallationId = installation.Id,
                    RepositoryId = Guid.Empty, CertificationRunId = run.Id, CertificationPass = pass,
                    CertificationFixtureKey = fixtureKey, CertificationFixtureResource = fixtureResource,
                    SourceRevision = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(
                        $"{installation.PackageVersion.PackageDigest ?? installation.PackageVersion.ManifestDigest}:{fixtureResource}"))),
                    RecipeKey = recipeKey, TargetKey = target,
                    ConfigurationJson = JsonSerializer.Serialize(new { certification = true, pass, fixtureKey, fixtureResource,
                        expectedCheckKeys = fixture.GetProperty("expectedCheckKeys") }),
                    DefinitionDigest = definition.DefinitionDigest, Status = W.DeliveryBuildStatuses.Queued, MaximumAttempts = 3,
                    IdempotencyKey = $"certification:{run.Id:N}:{recipeKey}:{target}:{fixtureKey}:{pass}",
                    RequestedByOrganizationUserId = owner.Id, CreatedAt = now, UpdatedAt = now };
                db.DeliveryBuilds.Add(build);
                scheduledBuilds.Add(build);
                var context = new W.AgentWorkContext(request.OrganizationId, workstream.Id, null, null, null, null, null,
                    Guid.NewGuid(), null, workstream.ProfileKey);
                var data = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "DeliveryBuild", build.Id,
                    build.Revision, recipeKey, "queued", JsonSerializer.SerializeToElement(new { buildId = build.Id, certificationRunId = run.Id, pass,
                        fixtureKey, fixtureResource, providerInstallationId = installation.Id, definition.DefinitionDigest }));
                db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem { Id = Guid.NewGuid(),
                    OrganizationId = request.OrganizationId, TargetInstallationId = installation.Id,
                    EventType = W.WorkstreamEventNames.BuildRequestedV1, DataJson = JsonSerializer.Serialize(data),
                    IdempotencyKey = $"{W.WorkstreamEventNames.BuildRequestedV1}:{build.Id:N}:1",
                    Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
            }
        }
        await db.SaveChangesAsync(token);
        foreach (var build in scheduledBuilds)
            await QueueCertificationBuildAsync(
                request, build, definition, installation, workloads, guestImages, db, token);
        var total = await db.DeliveryBuilds.CountAsync(x => x.CertificationRunId == run.Id, token);
        return Results.Accepted($"/api/admin/toolchain-certifications/{run.Id:D}", Map(run, new { Total = total, Complete = 0 }));
    }

    internal static async Task QueueCertificationBuildAsync(
        StartToolchainCertificationRequest request,
        DeliveryBuildRecord build,
        ToolchainAdapterDefinitionRecord definition,
        AgentInstallation installation,
        IExecutionWorkloadOrchestrator workloads,
        IGuestImageRegistry guestImages,
        CSweetDbContext db,
        CancellationToken token)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        if (db.Database.IsNpgsql())
        {
            var key = $"toolchain-build:{build.Id:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);
            await db.Entry(build).ReloadAsync(token);
        }
        if (build.Status != W.DeliveryBuildStatuses.Queued || await db.ExecutionWorkloadAssignments.AnyAsync(x =>
            x.DeliveryBuildId == build.Id && (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned ||
                x.Status == ExecutionAssignmentStatus.Starting || x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping), token))
            return;
        var package = installation.PackageVersion!;
        if (string.IsNullOrWhiteSpace(package.PackageDigest) || string.IsNullOrWhiteSpace(package.ArtifactSignature) ||
            string.IsNullOrWhiteSpace(package.ProjectPath))
            throw new InvalidOperationException("Certification requires the exact signed provider package artifact.");
        var artifactDigest = NormalizeDigest(package.PackageDigest);
        var (operatingSystem, architecture) = EnvironmentPlatform(request.EnvironmentProfileKey);
        var image = await guestImages.ResolveAsync(new GuestImageResolutionRequest(
            request.EnvironmentProfileKey, null, operatingSystem, architecture,
            AgentTrustLevel.OrganizationApproved, "1.0", ExpectedDigest: request.EnvironmentImageDigest), token);
        using var document = JsonDocument.Parse(definition.DefinitionJson);
        var root = document.RootElement;
        var maximumOutputBytes = checked(root.GetProperty("outputPolicy").GetProperty("maximumTotalBytes").GetInt64() + 64L * 1024 * 1024);
        var allowedRegistries = root.TryGetProperty("allowedDependencyRegistryHosts", out var registries)
            ? registries.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];
        const long maximumSourceBytes = 512L * 1024 * 1024;
        var now = DateTimeOffset.UtcNow;
        var workloadToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(), TickId = Guid.NewGuid(), AgentInstallationId = installation.Id,
            DeliveryBuildId = build.Id,
            BrokerTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workloadToken))),
            QueuedAt = now, RuntimeDeadlineAt = now.AddMinutes(35)
        };
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync(token);
        var limits = new WorkloadResourceLimits(4, 400, 8192,
            checked((int)Math.Clamp((maximumSourceBytes + maximumOutputBytes * 2) / (1024 * 1024), 1024, 1_048_576)),
            512, 32 * 1024 * 1024, TimeSpan.FromMinutes(30));
        var workload = new ToolchainBuildWorkloadSpecification(
            runtime.Id, image, limits,
            new BrokerChannelLease(Guid.NewGuid(), "1.0", workloadToken, image.Digest, artifactDigest, now.AddMinutes(35)),
            new AgentArtifactReference(artifactDigest, package.ArtifactSignature!, package.ArtifactFormatVersion,
                package.ArtifactOperatingSystem, package.ArtifactArchitecture),
            new RuntimeAgentIdentity(installation.Id, request.OrganizationId.ToString("D"), runtime.TickId,
                package.AgentName, "Toolchain certification provider"),
            new RepositoryDescriptor("https://fixtures.invalid/csweet/toolchain.git", build.SourceRevision, false,
                build.RecipeKey, "1.0"),
            build.Id, build.Revision, build.RecipeKey, build.TargetKey, build.ConfigurationJson,
            [Path.GetFileNameWithoutExtension(package.ProjectPath!)], allowedRegistries,
            maximumSourceBytes, maximumOutputBytes);
        await workloads.SubmitAsync(new ExecutionWorkloadRequest(
            ExecutionWorkloadKind.ToolchainBuild, null, runtime.Id, installation.ExecutionPoolId,
            request.OrganizationId.ToString("D"), null, image.Digest, artifactDigest,
            limits.VirtualCpuCount, limits.MemoryMegabytes, limits.WritableDiskMegabytes,
            JsonSerializer.Serialize(workload), DeliveryBuildId: build.Id), token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    internal static async Task<IResult> ResumeAsync(Guid id, ResumeToolchainCertificationRequest request, CSweetDbContext db,
        IExecutionWorkloadOrchestrator workloads, IGuestImageRegistry guestImages, CancellationToken token)
    {
        var run = await db.ToolchainCertificationRuns.SingleOrDefaultAsync(x => x.Id == id, token);
        if (run is null) return Results.NotFound();
        if (run.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "stale_certification", run.Revision });
        if (run.Status != W.ToolchainCertificationStatuses.Running || run.ExpiresAt <= DateTimeOffset.UtcNow)
            return Results.Conflict(new { error = "certification_not_running" });
        var setup = await ReadSetupAsync(run.OrganizationId, db, token);
        if (!setup.Providers.Any(x => x.InstallationId == run.ProviderInstallationId && x.DefinitionId == run.ToolchainDefinitionId &&
            x.EnvironmentProfiles.Contains(run.EnvironmentProfileKey)) || !setup.Images.Any(x => x.Digest == run.EnvironmentImageDigest))
            return Results.Conflict(new { error = "certification_capacity_changed" });
        var installation = await db.AgentInstallations.Include(x => x.PackageVersion).SingleAsync(x => x.Id == run.ProviderInstallationId, token);
        var definition = await db.ToolchainAdapterDefinitions.SingleAsync(x => x.Id == run.ToolchainDefinitionId, token);
        if (NormalizeDigest(installation.PackageVersion!.PackageDigest!) != run.ProviderPackageDigest || definition.DefinitionDigest != run.DefinitionDigest)
            return Results.Conflict(new { error = "certification_inputs_changed" });
        var builds = await db.DeliveryBuilds.Where(x => x.CertificationRunId == run.Id).ToListAsync(token);
        // An explicit administrator retry can recover pre-execution dispatch exhaustion. It
        // cannot turn a failed fixture or an exhausted provider execution into another attempt.
        foreach (var build in builds.Where(x => x.Status == W.DeliveryBuildStatuses.Exhausted && x.Attempt == 0 &&
            x.FailureCode == "headquarters-broker-rejected"))
        {
            if (await db.ExecutionWorkloadAssignments.AnyAsync(x => x.DeliveryBuildId == build.Id &&
                (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned ||
                 x.Status == ExecutionAssignmentStatus.Starting || x.Status == ExecutionAssignmentStatus.Running || x.Status == ExecutionAssignmentStatus.Stopping), token))
                continue;
            build.Status = W.DeliveryBuildStatuses.Queued; build.Revision++; build.UpdatedAt = DateTimeOffset.UtcNow;
        }
        run.Revision++;
        await db.SaveChangesAsync(token);
        var start = new StartToolchainCertificationRequest(run.OrganizationId, run.ToolchainDefinitionId, run.ProviderInstallationId,
            run.EnvironmentProfileKey, run.EnvironmentImageDigest);
        foreach (var build in builds.Where(x => x.Status == W.DeliveryBuildStatuses.Queued))
            await QueueCertificationBuildAsync(start, build, definition, installation, workloads, guestImages, db, token);
        return Results.Ok(Map(run, new { Total = builds.Count, Complete = builds.Count(x => x.Status == W.DeliveryBuildStatuses.Succeeded) }));
    }

    private static string NormalizeDigest(string value)
    {
        var digest = value.StartsWith("sha256:", StringComparison.Ordinal) ? value : $"sha256:{value}";
        if (digest.Length != 71 || digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new InvalidOperationException("The provider package artifact digest is invalid.");
        return digest;
    }

    private static (string OperatingSystem, string Architecture) EnvironmentPlatform(string profileKey) => (
        profileKey.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "windows" : "linux",
        profileKey.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64");

    private static async Task<IResult> RevokeAsync(Guid id, RevokeToolchainCertificationRequest request, CSweetDbContext db, CancellationToken token)
    {
        var run = await db.ToolchainCertificationRuns.SingleOrDefaultAsync(x => x.Id == id, token);
        if (run is null) return Results.NotFound();
        if (run.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "stale_certification", run.Revision });
        if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { error = "revocation_reason_required" });
        var now = DateTimeOffset.UtcNow; run.Status = W.ToolchainCertificationStatuses.Revoked;
        run.RevocationReason = request.Reason.Trim(); run.CompletedAt ??= now; run.Revision++;
        var eligibility = await db.ToolchainInstallationEligibilities.Where(x => x.CertificationRunId == run.Id).ToListAsync(token);
        foreach (var item in eligibility) { item.RevokedAt = now; item.RevocationReason = run.RevocationReason; }
        await db.SaveChangesAsync(token); return Results.Ok(Map(run, null));
    }

    private static ToolchainCertificationSummary Map(ToolchainCertificationRunRecord run, dynamic? counts) =>
        new(run.Id, run.OrganizationId, run.ToolchainDefinitionId, run.ProviderInstallationId,
            run.EnvironmentProfileKey, run.EnvironmentImageDigest, run.Status, counts?.Total ?? 0,
            counts?.Complete ?? 0, run.ChecksJson, run.FirstManifestHash, run.SecondManifestHash,
            run.RevocationReason, run.CreatedAt, run.CompletedAt, run.ExpiresAt, run.Revision);
}
