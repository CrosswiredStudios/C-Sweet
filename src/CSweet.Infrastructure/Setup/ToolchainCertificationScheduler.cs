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

namespace CSweet.Infrastructure.Setup;

/// <summary>Shared certification creation/dispatch for administrators and approved resource fulfillment.</summary>
public static class ToolchainCertificationScheduler
{
    public static async Task<ToolchainCertificationRunRecord> StartAsync(
        StartToolchainCertificationRequest request, ToolchainAdapterDefinitionRecord definition, AgentInstallation installation,
        IExecutionWorkloadOrchestrator workloads, IGuestImageRegistry guestImages, CSweetDbContext db, Guid runId, CancellationToken token)
    {
        var retained = await db.ToolchainCertificationRuns.SingleOrDefaultAsync(x => x.Id == runId, token);
        if (retained is not null)
        {
            if (retained.OrganizationId != request.OrganizationId || retained.ProviderInstallationId != installation.Id ||
                retained.ToolchainDefinitionId != definition.Id || retained.EnvironmentImageDigest != request.EnvironmentImageDigest)
                throw new InvalidOperationException("Certification identity is already bound to another plan.");
            if (retained.Status is W.ToolchainCertificationStatuses.Running or W.ToolchainCertificationStatuses.Pending)
                foreach (var build in await db.DeliveryBuilds.Where(x => x.CertificationRunId == runId && x.Status == W.DeliveryBuildStatuses.Queued).ToListAsync(token))
                    await QueueCertificationBuildAsync(request, build, definition, installation, workloads, guestImages, db, token);
            return retained;
        }
        var owner = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == request.OrganizationId && x.IsActive)
            .OrderByDescending(x => x.PermissionLevel).FirstOrDefaultAsync(token);
        if (owner is null) throw new InvalidOperationException("organization_has_no_active_owner");
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
        var run = new ToolchainCertificationRunRecord { Id = runId, OrganizationId = request.OrganizationId,
            ToolchainDefinitionId = definition.Id, ProviderInstallationId = installation.Id,
            EnvironmentProfileKey = request.EnvironmentProfileKey, EnvironmentImageDigest = request.EnvironmentImageDigest,
            ProviderPackageDigest = NormalizeDigest(installation.PackageVersion.PackageDigest ??
                throw new InvalidOperationException("Certification requires a built provider artifact.")),
            DefinitionDigest = definition.DefinitionDigest,
            Status = W.ToolchainCertificationStatuses.Running, CreatedAt = now, ExpiresAt = now.AddDays(request.ValidForDays) };
        db.ToolchainCertificationRuns.Add(run);
        using var definitionJson = JsonDocument.Parse(definition.DefinitionJson);
        var recipes = definitionJson.RootElement.GetProperty("recipes").EnumerateArray().ToList();
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
        return run;
    }

    public static async Task QueueCertificationBuildAsync(
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

}
