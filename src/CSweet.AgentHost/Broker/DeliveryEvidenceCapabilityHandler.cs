using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Formats.Tar;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Office.Contracts.Workloads;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

/// <summary>Domain-neutral build, preview, evaluation, and release evidence boundary.</summary>
public sealed class DeliveryEvidenceCapabilityHandler : IPlatformCapabilityHandler
{
    private readonly CSweetDbContext db;
    private readonly TimeProvider clock;
    private readonly IExecutionWorkloadOrchestrator? workloads;
    private readonly IGuestImageRegistry? guestImages;
    private readonly IAgentArtifactStore? artifactStore;

    public DeliveryEvidenceCapabilityHandler(CSweetDbContext db, TimeProvider clock)
        : this(db, clock, null, null, null) { }

    public DeliveryEvidenceCapabilityHandler(
        CSweetDbContext db,
        TimeProvider clock,
        IExecutionWorkloadOrchestrator? workloads,
        IGuestImageRegistry? guestImages,
        IAgentArtifactStore? artifactStore)
    {
        this.db = db;
        this.clock = clock;
        this.workloads = workloads;
        this.guestImages = guestImages;
        this.artifactStore = artifactStore;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        W.DeliveryEvidenceCapabilityNames.ToolchainCatalogReadV2,
        W.DeliveryEvidenceCapabilityNames.BuildRequestV2,
        W.DeliveryEvidenceCapabilityNames.BuildReadV2,
        W.DeliveryEvidenceCapabilityNames.BuildClaimV1,
        W.DeliveryEvidenceCapabilityNames.BuildHeartbeatV1,
        W.DeliveryEvidenceCapabilityNames.BuildReportV2,
        W.DeliveryEvidenceCapabilityNames.BuildCancelV1,
        W.DeliveryEvidenceCapabilityNames.ValidationReadV2,
        W.DeliveryEvidenceCapabilityNames.PreviewCreateV2,
        W.DeliveryEvidenceCapabilityNames.PreviewReadV2,
        W.DeliveryEvidenceCapabilityNames.EvaluationPlanV1,
        W.DeliveryEvidenceCapabilityNames.EvaluationReadV1,
        W.DeliveryEvidenceCapabilityNames.EvaluationReportV1,
        W.DeliveryEvidenceCapabilityNames.ReleaseReadinessReadV1,
        W.DeliveryEvidenceCapabilityNames.ReleaseReadinessSubmitV1,
        W.DeliveryEvidenceCapabilityNames.PublicationProposeV1
    };

    public bool CanHandle(string capability) => Capabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(AgentSession session, RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken token)
    {
        if (session.Grant.RequestedCapabilities?.Contains(request.Capability, StringComparer.Ordinal) != true)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The installation is not granted this delivery capability.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) || !Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent identity is invalid.");
        var actor = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive && x.EmployeeType == EmployeeType.Agent, token);
        if (actor is null) return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The agent is not an active employee.");
        try
        {
            object result = request.Capability switch
            {
                W.DeliveryEvidenceCapabilityNames.ToolchainCatalogReadV2 => await ReadToolchainsAsync(organizationId, Read<W.ReadToolchainCatalogV2Request>(request), token),
                W.DeliveryEvidenceCapabilityNames.BuildRequestV2 => await RequestBuildAsync(organizationId, installationId, actor.Id, Read<W.RequestBuildV2Request>(request), token),
                W.DeliveryEvidenceCapabilityNames.BuildReadV2 => await ReadBuildsAsync(organizationId, actor.Id, Read<W.ReadBuildV2Request>(request), token),
                W.DeliveryEvidenceCapabilityNames.BuildClaimV1 => await ClaimBuildAsync(organizationId, installationId, session.RuntimeInstanceId, Read<W.ClaimBuildRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.BuildHeartbeatV1 => await HeartbeatBuildAsync(organizationId, installationId, Read<W.HeartbeatBuildRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.BuildReportV2 => await ReportBuildAsync(organizationId, installationId, Read<W.ReportBuildV2Request>(request), token),
                W.DeliveryEvidenceCapabilityNames.BuildCancelV1 => await CancelBuildAsync(organizationId, actor.Id, Read<W.CancelBuildRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.ValidationReadV2 => await ReadValidationsAsync(organizationId, actor.Id, Read<W.ReadValidationRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.PreviewCreateV2 => await CreatePreviewAsync(organizationId, actor.Id, Read<W.CreatePreviewV2Request>(request), token),
                W.DeliveryEvidenceCapabilityNames.PreviewReadV2 => await ReadPreviewsAsync(organizationId, actor.Id, Read<W.ReadPreviewV2Request>(request), token),
                W.DeliveryEvidenceCapabilityNames.EvaluationPlanV1 => await PlanEvaluationAsync(organizationId, actor.Id, Read<W.PlanEvaluationSessionRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.EvaluationReadV1 => await ReadEvaluationsAsync(organizationId, actor.Id, Read<W.ReadEvaluationSessionRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.EvaluationReportV1 => await ReportEvaluationAsync(organizationId, actor.Id, Read<W.ReportEvaluationSessionRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.ReleaseReadinessReadV1 => await ReadReleaseAsync(organizationId, actor.Id, Read<W.ReadReleaseReadinessRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.ReleaseReadinessSubmitV1 => await SubmitReleaseAsync(organizationId, actor.Id, Read<W.SubmitReleaseReadinessRequest>(request), token),
                W.DeliveryEvidenceCapabilityNames.PublicationProposeV1 => await ProposePublicationAsync(organizationId, installationId, actor.Id, Read<W.PublicationProposalRequest>(request), token),
                _ => throw new KeyNotFoundException("The delivery capability is not implemented.")
            };
            return Success(request.RequestId, result);
        }
        catch (JsonException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (InvalidDataException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (ArgumentException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message); }
        catch (UnauthorizedAccessException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, exception.Message); }
        catch (DbUpdateConcurrencyException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        catch (InvalidOperationException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, exception.Message); }
        catch (KeyNotFoundException exception) { return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, exception.Message); }
    }

    private async Task<IReadOnlyList<W.EligibleToolchainAdapter>> ReadToolchainsAsync(
        Guid organizationId, W.ReadToolchainCatalogV2Request request, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var eligibility = await db.ToolchainInstallationEligibilities.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.RevokedAt == null && x.ExpiresAt > now)
            .ToListAsync(token);
        var activeInstallations = await db.AgentInstallations.AsNoTracking().Where(x =>
                eligibility.Select(item => item.ProviderInstallationId).Contains(x.Id) && x.IsEnabled &&
                x.RevisionStatus == CSweet.Domain.Setup.PluginRevisionStatus.Active &&
                x.SetupState == CSweet.Domain.Setup.PluginSetupState.Ready)
            .Select(x => x.Id).ToListAsync(token);
        eligibility = eligibility.Where(x => activeInstallations.Contains(x.ProviderInstallationId)).ToList();
        var definitions = await db.ToolchainAdapterDefinitions.AsNoTracking()
            .Where(x => eligibility.Select(e => e.ToolchainDefinitionId).Contains(x.Id))
            .OrderBy(x => x.DisplayName).ToListAsync(token);
        var results = new List<W.EligibleToolchainAdapter>();
        foreach (var definition in definitions)
        {
            var parsed = MapToolchain(definition);
            var recipes = parsed.Recipes.Where(recipe =>
                (string.IsNullOrWhiteSpace(request.RecipeKey) || recipe.Key == request.RecipeKey) &&
                (request.RequiredOperations is not { Count: > 0 } || request.RequiredOperations.All(op => recipe.Operations.Contains(op, StringComparer.Ordinal))) &&
                (request.TargetKeys is not { Count: > 0 } || request.TargetKeys.Any(target => recipe.TargetKeys.Contains(target, StringComparer.Ordinal)))).ToList();
            if (recipes.Count == 0) continue;
            foreach (var item in eligibility.Where(x => x.ToolchainDefinitionId == definition.Id))
            {
                var compatibleCapacityOnline = await db.ExecutionNodeProviders.AsNoTracking().AnyAsync(provider =>
                    provider.IsAvailable && provider.SupportsToolchainBuildWorkloads &&
                    provider.GuestImageDigest == item.EnvironmentImageDigest && provider.ExecutionNode != null &&
                    provider.ExecutionNode.Status == CSweet.Domain.Setup.ExecutionNodeStatus.Ready &&
                    provider.ExecutionNode.LastHeartbeatAt >= now.AddMinutes(-2), token);
                results.Add(new W.EligibleToolchainAdapter(parsed with { Recipes = recipes }, new W.ToolchainEligibility(
                    definition.Id, item.ProviderInstallationId, item.CertificationRunId, item.EnvironmentProfileKey,
                    item.EnvironmentImageDigest, item.CertifiedAt, item.ExpiresAt, compatibleCapacityOnline)));
            }
        }
        return results.Where(x => x.Eligibility.CompatibleCapacityOnline).ToList();
    }

    private async Task<W.DeliveryBuildV2> RequestBuildAsync(Guid organizationId, Guid installationId, Guid actorId,
        W.RequestBuildV2Request request, CancellationToken token)
    {
        var workstream = await RequireWorkstreamAsync(organizationId, actorId, request.WorkstreamId, token);
        Text(request.SourceRevision, 1, 200, "Source revision"); Text(request.RecipeKey, 1, 200, "Recipe key");
        if (!IsGitCommit(request.SourceRevision))
            throw new ArgumentException("Source revision must be an exact lowercase 40-character Git commit SHA.");
        Text(request.TargetKey, 1, 200, "Target key"); Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.Configuration.ValueKind != JsonValueKind.Object) throw new ArgumentException("Build configuration must be an object.");
        if (request.MaximumAttempts is < 1 or > 5) throw new ArgumentException("Maximum attempts must be between one and five.");
        if (request.TeamId.HasValue && !await db.WorkstreamTeamAssignments.AsNoTracking().AnyAsync(x =>
            x.WorkstreamId == workstream.Id && x.TeamId == request.TeamId && x.EndsAt == null, token))
            throw new ArgumentException("The build team is not assigned to this Workstream.");
        var existing = await db.DeliveryBuilds.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.RequestedByInstallationId == installationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapBuild(existing);
        var now = clock.GetUtcNow();
        var eligible = await db.ToolchainInstallationEligibilities.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.ToolchainDefinitionId == request.ToolchainDefinitionId &&
            x.ProviderInstallationId == request.ProviderInstallationId && x.RevokedAt == null && x.ExpiresAt > now, token)
            ?? throw new ArgumentException("The exact adapter definition and provider installation are not certified or eligible.");
        var definition = await db.ToolchainAdapterDefinitions.AsNoTracking().SingleAsync(x => x.Id == request.ToolchainDefinitionId, token);
        var contract = MapToolchain(definition);
        var recipe = contract.Recipes.SingleOrDefault(x => x.Key == request.RecipeKey)
            ?? throw new ArgumentException("The adapter does not declare the requested recipe.");
        if (!recipe.Operations.Contains("build", StringComparer.Ordinal) || !recipe.TargetKeys.Contains(request.TargetKey, StringComparer.Ordinal))
            throw new ArgumentException("The recipe does not support the requested build target.");
        var repository = await db.SourceControlRepositories.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.RepositoryId && x.OrganizationId == organizationId &&
            x.Status == SourceControlRepositoryStatus.Ready && x.ArchivedAt == null, token)
            ?? throw new ArgumentException("The source repository is not ready or does not belong to this organization.");
        var provider = await db.AgentInstallations.AsNoTracking()
            .Include(x => x.PackageVersion)
            .SingleOrDefaultAsync(x => x.Id == eligible.ProviderInstallationId && x.BusinessId == organizationId.ToString("D") &&
                x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active && x.SetupState == PluginSetupState.Ready, token)
            ?? throw new ArgumentException("The selected toolchain provider installation is not active.");
        var package = provider.PackageVersion
            ?? throw new InvalidOperationException("The toolchain provider package is unavailable.");
        if (!string.Equals(package.AgentId, definition.ProviderPackageId, StringComparison.Ordinal) ||
            !string.Equals(package.Version, definition.ProviderPackageVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(package.PackageDigest) || string.IsNullOrWhiteSpace(package.ArtifactSignature) ||
            string.IsNullOrWhiteSpace(package.ProjectPath))
            throw new InvalidOperationException("The selected provider does not have the exact certified, signed package artifact.");

        var build = new DeliveryBuildRecord
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = request.WorkstreamId,
            TeamId = request.TeamId, ToolchainDefinitionId = definition.Id, ProviderInstallationId = eligible.ProviderInstallationId,
            RepositoryId = request.RepositoryId, SourceRevision = request.SourceRevision, RecipeKey = request.RecipeKey,
            TargetKey = request.TargetKey, ConfigurationJson = request.Configuration.GetRawText(), DefinitionDigest = definition.DefinitionDigest,
            Status = W.DeliveryBuildStatuses.Queued, MaximumAttempts = request.MaximumAttempts,
            IdempotencyKey = request.IdempotencyKey, RequestedByOrganizationUserId = actorId,
            RequestedByInstallationId = installationId, CreatedAt = now, UpdatedAt = now
        };
        db.DeliveryBuilds.Add(build);
        AddEvent(W.WorkstreamEventNames.BuildRequestedV1, workstream, build.Id, build.Revision,
            request.RecipeKey, "queued", new { build.Id, providerInstallationId = eligible.ProviderInstallationId,
                definition.DefinitionDigest, request.RecipeKey, request.TargetKey, request.SourceRevision }, eligible.ProviderInstallationId);
        await db.SaveChangesAsync(token);
        await QueueToolchainWorkloadAsync(
            organizationId, build, eligible, definition, provider, package, repository, contract, token);
        return MapBuild(build);
    }

    private async Task<W.DeliveryBuildV2> ClaimBuildAsync(
        Guid organizationId, Guid installationId, string runtimeInstanceId, W.ClaimBuildRequest request, CancellationToken token)
    {
        var build = await db.DeliveryBuilds.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.Id == request.BuildId, token)
            ?? throw new KeyNotFoundException("The delivery build was not found.");
        if (build.ProviderInstallationId != installationId)
            throw new UnauthorizedAccessException("Only the provider installation selected for this build may claim it.");
        if (build.Revision != request.ExpectedRevision)
            throw new DbUpdateConcurrencyException("The build changed before it could be claimed.");
        Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.LeaseDuration < TimeSpan.FromMinutes(1) || request.LeaseDuration > TimeSpan.FromMinutes(30))
            throw new ArgumentException("A build lease must be between one and thirty minutes.");
        if (!Guid.TryParse(runtimeInstanceId, out var runtimeId)) throw new UnauthorizedAccessException("The runtime identity is invalid.");
        var assignment = await db.ExecutionWorkloadAssignments.AsNoTracking().SingleOrDefaultAsync(x =>
                x.AgentRuntimeInstanceId == runtimeId && x.DeliveryBuildId == build.Id &&
                x.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild &&
                x.Status == ExecutionAssignmentStatus.Running, token)
            ?? throw new UnauthorizedAccessException("The build claim is not bound to this exact managed Office workload.");
        if (!assignment.ExecutionNodeId.HasValue)
            throw new InvalidOperationException("The managed Office workload has no execution node binding.");
        var now = clock.GetUtcNow();
        if (build.Status == W.DeliveryBuildStatuses.Claimed && build.LeaseExpiresAt > now)
            throw new InvalidOperationException("The build already has an active lease.");
        if (build.Attempt >= build.MaximumAttempts)
        {
            build.Status = W.DeliveryBuildStatuses.Exhausted; build.Revision++; build.UpdatedAt = now;
            await db.SaveChangesAsync(token); return MapBuild(build);
        }
        build.ClaimId = Guid.NewGuid(); build.ExecutionNodeId = assignment.ExecutionNodeId;
        build.LeaseExpiresAt = now + request.LeaseDuration; build.LastHeartbeatAt = now;
        build.Attempt++; build.Status = W.DeliveryBuildStatuses.Claimed; build.Revision++; build.UpdatedAt = now;
        await db.SaveChangesAsync(token); return MapBuild(build);
    }

    private async Task QueueToolchainWorkloadAsync(
        Guid organizationId,
        DeliveryBuildRecord build,
        ToolchainInstallationEligibilityRecord eligible,
        ToolchainAdapterDefinitionRecord definition,
        AgentInstallation provider,
        AgentPackageVersion package,
        SourceControlRepository repository,
        W.ToolchainAdapterDefinition adapter,
        CancellationToken token)
    {
        if (workloads is null || guestImages is null)
            throw new InvalidOperationException("The isolated toolchain execution plane is unavailable.");

        var (operatingSystem, architecture) = EnvironmentPlatform(eligible.EnvironmentProfileKey);
        var image = await guestImages.ResolveAsync(new GuestImageResolutionRequest(
            eligible.EnvironmentProfileKey,
            null,
            operatingSystem,
            architecture,
            AgentTrustLevel.OrganizationApproved,
            "1.0",
            ExpectedDigest: eligible.EnvironmentImageDigest), token);
        var artifactDigest = NormalizeSha256(package.PackageDigest!, "toolchain package artifact");
        var maximumOutputBytes = checked(adapter.OutputPolicy.GetProperty("maximumTotalBytes").GetInt64() + 64L * 1024 * 1024);
        const long maximumSourceBytes = 512L * 1024 * 1024;
        var maximumDuration = TimeSpan.FromMinutes(30);
        var now = clock.GetUtcNow();
        var workloadToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = provider.Id,
            BrokerTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workloadToken))),
            QueuedAt = now,
            RuntimeDeadlineAt = now.Add(maximumDuration).AddMinutes(5)
        };
        db.AgentRuntimeInstances.Add(runtime);
        await db.SaveChangesAsync(token);

        var resourceLimits = new WorkloadResourceLimits(
            4,
            400,
            8192,
            checked((int)Math.Clamp((maximumSourceBytes + maximumOutputBytes * 2) / (1024 * 1024), 1024, 1_048_576)),
            512,
            32 * 1024 * 1024,
            maximumDuration);
        var root = JsonDocument.Parse(definition.DefinitionJson).RootElement;
        var allowedRegistries = root.TryGetProperty("allowedDependencyRegistryHosts", out var hosts) &&
                                hosts.ValueKind == JsonValueKind.Array
            ? hosts.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray()
            : [];
        var workload = new ToolchainBuildWorkloadSpecification(
            runtime.Id,
            image,
            resourceLimits,
            new BrokerChannelLease(
                Guid.NewGuid(), "1.0", workloadToken, image.Digest, artifactDigest,
                now.Add(maximumDuration).AddMinutes(5)),
            new AgentArtifactReference(
                artifactDigest,
                package.ArtifactSignature!,
                package.ArtifactFormatVersion,
                package.ArtifactOperatingSystem,
                package.ArtifactArchitecture),
            new RuntimeAgentIdentity(provider.Id, organizationId.ToString("D"), runtime.TickId,
                package.AgentName, "Toolchain build provider"),
            new RepositoryDescriptor(repository.CloneUrl, build.SourceRevision, false, build.RecipeKey, "1.0"),
            build.Id,
            build.Revision,
            build.RecipeKey,
            build.TargetKey,
            build.ConfigurationJson,
            [Path.GetFileNameWithoutExtension(package.ProjectPath!)],
            allowedRegistries,
            maximumSourceBytes,
            maximumOutputBytes);
        await workloads.SubmitAsync(new ExecutionWorkloadRequest(
            ExecutionWorkloadKind.ToolchainBuild,
            null,
            runtime.Id,
            provider.ExecutionPoolId,
            organizationId.ToString("D"),
            null,
            image.Digest,
            artifactDigest,
            resourceLimits.VirtualCpuCount,
            resourceLimits.MemoryMegabytes,
            resourceLimits.WritableDiskMegabytes,
            JsonSerializer.Serialize(workload, JsonOptions),
            DeliveryBuildId: build.Id), token);
    }

    private async Task<W.DeliveryBuildV2> HeartbeatBuildAsync(
        Guid organizationId, Guid installationId, W.HeartbeatBuildRequest request, CancellationToken token)
    {
        var build = await db.DeliveryBuilds.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == request.BuildId, token)
            ?? throw new KeyNotFoundException("The delivery build was not found.");
        RequireActiveClaim(build, installationId, request.ClaimId, request.ExpectedRevision);
        if (request.LeaseExtension < TimeSpan.FromMinutes(1) || request.LeaseExtension > TimeSpan.FromMinutes(30))
            throw new ArgumentException("A lease extension must be between one and thirty minutes.");
        var now = clock.GetUtcNow();
        build.LastHeartbeatAt = now; build.LeaseExpiresAt = now + request.LeaseExtension;
        build.Status = W.DeliveryBuildStatuses.Running; build.Revision++; build.UpdatedAt = now;
        await db.SaveChangesAsync(token); return MapBuild(build);
    }

    private async Task<W.DeliveryBuildV2> ReportBuildAsync(
        Guid organizationId, Guid installationId, W.ReportBuildV2Request request, CancellationToken token)
    {
        var build = await db.DeliveryBuilds.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == request.BuildId, token)
            ?? throw new KeyNotFoundException("The delivery build was not found.");
        RequireActiveClaim(build, installationId, request.ClaimId, request.ExpectedRevision);
        var status = request.Status.Trim() switch
        {
            W.DeliveryBuildStatuses.Running => W.DeliveryBuildStatuses.Running,
            W.DeliveryBuildStatuses.Succeeded => W.DeliveryBuildStatuses.Succeeded,
            W.DeliveryBuildStatuses.Failed => W.DeliveryBuildStatuses.Failed,
            W.DeliveryBuildStatuses.Blocked => W.DeliveryBuildStatuses.Blocked,
            W.DeliveryBuildStatuses.Cancelled => W.DeliveryBuildStatuses.Cancelled,
            _ => throw new ArgumentException("Build status must be Running, Succeeded, Failed, Blocked, or Cancelled.")
        };
        if (status == W.DeliveryBuildStatuses.Succeeded && request.Outputs.Count == 0)
            throw new ArgumentException("A successful build report requires an output manifest.");
        var definition = await db.ToolchainAdapterDefinitions.AsNoTracking()
            .SingleAsync(x => x.Id == build.ToolchainDefinitionId, token);
        var adapter = MapToolchain(definition);
        ValidateOutputs(request.Outputs, adapter.OutputPolicy, adapter.SupportedContentTypes);
        if (status == W.DeliveryBuildStatuses.Succeeded)
            await VerifyIngestedOutputsAsync(build, request.Outputs, token);
        var now = clock.GetUtcNow();
        string expectedEnvironmentDigest;
        if (build.CertificationRunId.HasValue)
        {
            var certification = await db.ToolchainCertificationRuns.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == build.CertificationRunId && x.OrganizationId == organizationId &&
                x.ToolchainDefinitionId == build.ToolchainDefinitionId &&
                x.ProviderInstallationId == installationId &&
                (x.Status == W.ToolchainCertificationStatuses.Pending || x.Status == W.ToolchainCertificationStatuses.Running), token)
                ?? throw new InvalidOperationException("The certification run is no longer active.");
            expectedEnvironmentDigest = certification.EnvironmentImageDigest;
        }
        else
        {
            var eligibility = await db.ToolchainInstallationEligibilities.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.ToolchainDefinitionId == build.ToolchainDefinitionId &&
                x.ProviderInstallationId == installationId && x.RevokedAt == null && x.ExpiresAt > now, token)
                ?? throw new InvalidOperationException("The build provider certification is no longer eligible.");
            expectedEnvironmentDigest = eligibility.EnvironmentImageDigest;
        }
        if (!string.Equals(request.Provenance.SourceRevision, build.SourceRevision, StringComparison.Ordinal) ||
            !string.Equals(request.Provenance.AdapterDefinitionDigest, build.DefinitionDigest, StringComparison.Ordinal))
            throw new ArgumentException("Build provenance does not match the requested source revision and adapter definition.");
        if (!string.Equals(request.Provenance.ProviderPackageId, definition.ProviderPackageId, StringComparison.Ordinal) ||
            !string.Equals(request.Provenance.ProviderPackageVersion, definition.ProviderPackageVersion, StringComparison.Ordinal))
            throw new ArgumentException("Build provenance does not match the certified provider package identity.");
        if (!string.Equals(request.Provenance.EnvironmentImageDigest, expectedEnvironmentDigest, StringComparison.Ordinal))
            throw new ArgumentException("Build provenance does not match the certified Office environment image.");
        if (request.Outputs.Count > 0 && !string.Equals(request.Provenance.NormalizedOutputManifestHash,
                ComputeNormalizedOutputManifestHash(request.Outputs), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Build provenance does not match the normalized output manifest.");
        if (status == W.DeliveryBuildStatuses.Succeeded &&
            (request.Provenance.ToolVersions.ValueKind != JsonValueKind.Object ||
             request.Provenance.Commands.Count == 0 || !IsSha256(request.Provenance.LockfileHash)))
            throw new ArgumentException("Successful build provenance requires tool versions, executed commands, and a source-lock SHA-256 digest.");
        var workstream = await db.Workstreams.SingleAsync(x => x.Id == build.WorkstreamId, token);
        build.Status = status;
        build.OutputsJson = JsonSerializer.Serialize(request.Outputs, JsonOptions);
        build.ProvenanceJson = JsonSerializer.Serialize(request.Provenance, JsonOptions);
        build.FailureCode = request.FailureCode; build.FailureSummary = request.FailureSummary;
        if (status != W.DeliveryBuildStatuses.Running) build.LeaseExpiresAt = null;
        build.Revision++;
        build.UpdatedAt = now;
        foreach (var report in request.Validations)
        {
            Text(report.TypeKey, 1, 200, "Validation type key");
            var validation = new DeliveryValidationRecord
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = build.WorkstreamId,
                BuildId = build.Id, TypeKey = report.TypeKey, Status = report.Status,
                Summary = report.Summary, FindingsJson = JsonSerializer.Serialize(report.Findings, JsonOptions),
                EvidenceJson = JsonSerializer.Serialize(report.Evidence, JsonOptions),
                CreatedAt = now, CompletedAt = now
            };
            db.DeliveryValidations.Add(validation);
            AddEvent(W.WorkstreamEventNames.ValidationCompletedV1, workstream, validation.Id, 1,
                validation.TypeKey, validation.Status.ToLowerInvariant(), new { validation.Id, buildId = build.Id, report.Summary, report.Findings });
        }
        if (status == W.DeliveryBuildStatuses.Succeeded)
            AddEvent(W.WorkstreamEventNames.BuildPublishedV1, workstream, build.Id, build.Revision,
                build.TargetKey, "succeeded", new { build.Id, build.SourceRevision, build.RecipeKey, build.TargetKey, request.Outputs });
        await db.SaveChangesAsync(token);
        return MapBuild(build);
    }

    private async Task VerifyIngestedOutputsAsync(
        DeliveryBuildRecord build,
        IReadOnlyList<W.BuildOutputManifestEntry> outputs,
        CancellationToken token)
    {
        var assignment = await db.ExecutionWorkloadAssignments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.DeliveryBuildId == build.Id && x.WorkloadKind == ExecutionWorkloadKind.ToolchainBuild &&
            x.ResultArtifactDigest != null, token)
            ?? throw new InvalidOperationException("A successful build requires its bounded output bundle to be ingested first.");
        if (artifactStore is null)
            throw new InvalidOperationException("The immutable build output store is unavailable.");
        await using var bundle = await artifactStore.OpenReadAsync(assignment.ResultArtifactDigest!, token);
        using var reader = new TarReader(bundle, leaveOpen: true);
        var expected = outputs.ToDictionary(
            x => "payload/output/" + x.RelativePath.Replace('\\', '/'),
            StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            if (!name.StartsWith("payload/output/", StringComparison.Ordinal)) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream is null || !expected.TryGetValue(name, out var declared) || !observed.Add(name))
                throw new InvalidDataException("The ingested output bundle contains undeclared, duplicate, or non-file output.");
            if (entry.Length != declared.Size)
                throw new InvalidDataException("An ingested output size does not match the signed output manifest.");
            var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(entry.DataStream, token));
            if (!string.Equals(digest, declared.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("An ingested output digest does not match the signed output manifest.");
        }
        if (observed.Count != expected.Count)
            throw new InvalidDataException("The ingested output bundle is missing a declared build output.");
    }

    private async Task<W.DeliveryBuildV2> CancelBuildAsync(Guid organizationId, Guid actorId, W.CancelBuildRequest request, CancellationToken token)
    {
        var build = await db.DeliveryBuilds.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == request.BuildId, token)
            ?? throw new KeyNotFoundException("The delivery build was not found.");
        await RequireWorkstreamAsync(organizationId, actorId, build.WorkstreamId, token);
        if (build.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The build changed before cancellation.");
        Text(request.Reason, 1, 2048, "Cancellation reason"); Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        var now = clock.GetUtcNow(); build.CancellationReason = request.Reason; build.CancelRequestedAt = now;
        build.Status = build.ClaimId.HasValue ? W.DeliveryBuildStatuses.CancelRequested : W.DeliveryBuildStatuses.Cancelled;
        build.Revision++; build.UpdatedAt = now; await db.SaveChangesAsync(token);
        if (workloads is not null)
        {
            var assignmentId = await db.ExecutionWorkloadAssignments.AsNoTracking()
                .Where(x => x.DeliveryBuildId == build.Id &&
                    (x.Status == ExecutionAssignmentStatus.Pending || x.Status == ExecutionAssignmentStatus.Assigned ||
                     x.Status == ExecutionAssignmentStatus.Starting || x.Status == ExecutionAssignmentStatus.Running ||
                     x.Status == ExecutionAssignmentStatus.Stopping))
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            if (assignmentId.HasValue)
                await workloads.CancelAsync(assignmentId.Value, request.Reason, token);
        }
        return MapBuild(build);
    }

    private async Task<IReadOnlyList<W.DeliveryBuildV2>> ReadBuildsAsync(Guid organizationId, Guid actorId, W.ReadBuildV2Request request, CancellationToken token)
    {
        if (!request.BuildId.HasValue && !request.WorkstreamId.HasValue) throw new ArgumentException("BuildId or WorkstreamId is required.");
        var query = db.DeliveryBuilds.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (request.BuildId.HasValue) query = query.Where(x => x.Id == request.BuildId);
        if (request.WorkstreamId.HasValue) query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        foreach (var id in rows.Select(x => x.WorkstreamId).Distinct()) await RequireWorkstreamAsync(organizationId, actorId, id, token);
        return rows.Select(MapBuild).ToList();
    }

    private async Task<IReadOnlyList<W.DeliveryValidation>> ReadValidationsAsync(Guid organizationId, Guid actorId, W.ReadValidationRequest request, CancellationToken token)
    {
        if (!request.ValidationId.HasValue && !request.BuildId.HasValue && !request.WorkstreamId.HasValue) throw new ArgumentException("A validation, build, or Workstream id is required.");
        var query = db.DeliveryValidations.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (request.ValidationId.HasValue) query = query.Where(x => x.Id == request.ValidationId);
        if (request.BuildId.HasValue) query = query.Where(x => x.BuildId == request.BuildId);
        if (request.WorkstreamId.HasValue) query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        foreach (var id in rows.Select(x => x.WorkstreamId).Distinct()) await RequireWorkstreamAsync(organizationId, actorId, id, token);
        return rows.Select(MapValidation).ToList();
    }

    private async Task<W.DeliveryPreviewV2> CreatePreviewAsync(Guid organizationId, Guid actorId, W.CreatePreviewV2Request request, CancellationToken token)
    {
        await RequireWorkstreamAsync(organizationId, actorId, request.WorkstreamId, token); Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.Lifetime < TimeSpan.FromMinutes(5) || request.Lifetime > TimeSpan.FromDays(7)) throw new ArgumentException("Preview lifetime must be between five minutes and seven days.");
        var existing = await db.PreviewSessions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapPreview(existing);
        var build = await db.DeliveryBuilds.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.WorkstreamId == request.WorkstreamId && x.Id == request.BuildId, token)
            ?? throw new ArgumentException("The build was not found in this Workstream.");
        Text(request.Mode, 1, 100, "Preview mode");
        if (build.Status != W.DeliveryBuildStatuses.Succeeded) throw new InvalidOperationException("A preview requires a successful exact build.");
        var now = clock.GetUtcNow();
        var preview = new PreviewSessionRecord { Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = request.WorkstreamId,
            BuildId = build.Id, Mode = request.Mode, Status = "Requested",
            EvidenceJson = JsonSerializer.Serialize(request.EvidenceTypeKeys, JsonOptions), ExpiresAt = now + request.Lifetime, CreatedByOrganizationUserId = actorId,
            IdempotencyKey = request.IdempotencyKey, CreatedAt = now };
        db.PreviewSessions.Add(preview); await db.SaveChangesAsync(token); return MapPreview(preview);
    }

    private async Task<IReadOnlyList<W.DeliveryPreviewV2>> ReadPreviewsAsync(Guid organizationId, Guid actorId, W.ReadPreviewV2Request request, CancellationToken token)
    {
        if (!request.PreviewId.HasValue && !request.WorkstreamId.HasValue) throw new ArgumentException("PreviewId or WorkstreamId is required.");
        var query = db.PreviewSessions.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (request.PreviewId.HasValue) query = query.Where(x => x.Id == request.PreviewId);
        if (request.WorkstreamId.HasValue) query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        foreach (var id in rows.Select(x => x.WorkstreamId).Distinct()) await RequireWorkstreamAsync(organizationId, actorId, id, token);
        return rows.Select(MapPreview).ToList();
    }

    private async Task<W.EvaluationSession> PlanEvaluationAsync(Guid organizationId, Guid actorId, W.PlanEvaluationSessionRequest request, CancellationToken token)
    {
        await RequireWorkstreamAsync(organizationId, actorId, request.WorkstreamId, token);
        Text(request.TypeKey, 1, 200, "Evaluation type key"); Text(request.ConsentPolicyKey, 1, 200, "Consent policy key"); Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.Plan.ValueKind != JsonValueKind.Object) throw new ArgumentException("Evaluation plan must be an object.");
        if (request.BuildId.HasValue && !await db.DeliveryBuilds.AsNoTracking().AnyAsync(x => x.OrganizationId == organizationId && x.WorkstreamId == request.WorkstreamId && x.Id == request.BuildId, token))
            throw new ArgumentException("The evaluation build was not found in this Workstream.");
        var existing = await db.EvaluationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapEvaluation(existing);
        var now = clock.GetUtcNow();
        var session = new EvaluationSessionRecord { Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = request.WorkstreamId,
            BuildId = request.BuildId, TypeKey = request.TypeKey, PlanJson = request.Plan.GetRawText(), ConsentPolicyKey = request.ConsentPolicyKey,
            Status = "Planned", IdempotencyKey = request.IdempotencyKey, CreatedByOrganizationUserId = actorId, CreatedAt = now, UpdatedAt = now };
        db.EvaluationSessions.Add(session); await db.SaveChangesAsync(token); return MapEvaluation(session);
    }

    private async Task<IReadOnlyList<W.EvaluationSession>> ReadEvaluationsAsync(Guid organizationId, Guid actorId, W.ReadEvaluationSessionRequest request, CancellationToken token)
    {
        if (!request.EvaluationSessionId.HasValue && !request.WorkstreamId.HasValue) throw new ArgumentException("EvaluationSessionId or WorkstreamId is required.");
        var query = db.EvaluationSessions.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (request.EvaluationSessionId.HasValue) query = query.Where(x => x.Id == request.EvaluationSessionId);
        if (request.WorkstreamId.HasValue) query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        foreach (var id in rows.Select(x => x.WorkstreamId).Distinct()) await RequireWorkstreamAsync(organizationId, actorId, id, token);
        return rows.Select(MapEvaluation).ToList();
    }

    private async Task<W.EvaluationSession> ReportEvaluationAsync(Guid organizationId, Guid actorId, W.ReportEvaluationSessionRequest request, CancellationToken token)
    {
        var session = await db.EvaluationSessions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == request.EvaluationSessionId, token)
            ?? throw new KeyNotFoundException("The evaluation session was not found.");
        var workstream = await RequireWorkstreamAsync(organizationId, actorId, session.WorkstreamId, token);
        if (session.Revision != request.ExpectedRevision) throw new DbUpdateConcurrencyException("The evaluation session changed; reload it before reporting.");
        if (request.Report.ValueKind != JsonValueKind.Object) throw new ArgumentException("Evaluation report must be an object.");
        var now = clock.GetUtcNow(); session.ReportJson = request.Report.GetRawText(); session.EvidenceJson = JsonSerializer.Serialize(request.Evidence, JsonOptions);
        session.Status = "Completed"; session.Revision++; session.UpdatedAt = now;
        AddEvent(W.WorkstreamEventNames.EvaluationCompletedV1, workstream, session.Id, session.Revision, session.TypeKey, "completed", new { session.Id, request.Evidence });
        await db.SaveChangesAsync(token); return MapEvaluation(session);
    }

    private async Task<IReadOnlyList<W.ReleaseReadiness>> ReadReleaseAsync(Guid organizationId, Guid actorId, W.ReadReleaseReadinessRequest request, CancellationToken token)
    {
        if (!request.ReleaseReadinessId.HasValue && !request.WorkstreamId.HasValue) throw new ArgumentException("ReleaseReadinessId or WorkstreamId is required.");
        var query = db.ReleaseReadinessRecords.AsNoTracking().Where(x => x.OrganizationId == organizationId);
        if (request.ReleaseReadinessId.HasValue) query = query.Where(x => x.Id == request.ReleaseReadinessId);
        if (request.WorkstreamId.HasValue) query = query.Where(x => x.WorkstreamId == request.WorkstreamId);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        foreach (var id in rows.Select(x => x.WorkstreamId).Distinct()) await RequireWorkstreamAsync(organizationId, actorId, id, token);
        return rows.Select(MapRelease).ToList();
    }

    private async Task<W.ReleaseReadiness> SubmitReleaseAsync(Guid organizationId, Guid actorId, W.SubmitReleaseReadinessRequest request, CancellationToken token)
    {
        var workstream = await RequireWorkstreamAsync(organizationId, actorId, request.WorkstreamId, token);
        Text(request.TypeKey, 1, 200, "Release type key"); Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        if (request.Evidence.Count == 0) throw new ArgumentException("Release readiness requires evidence.");
        var existing = await db.ReleaseReadinessRecords.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return MapRelease(existing);
        var now = clock.GetUtcNow(); var status = request.Findings.Any(x => x.Blocking) ? "Blocked" : "Ready";
        var readiness = new ReleaseReadinessRecord { Id = Guid.NewGuid(), OrganizationId = organizationId, WorkstreamId = request.WorkstreamId,
            TypeKey = request.TypeKey, Status = status, EvidenceJson = JsonSerializer.Serialize(request.Evidence, JsonOptions),
            FindingsJson = JsonSerializer.Serialize(request.Findings, JsonOptions), IdempotencyKey = request.IdempotencyKey, CreatedAt = now, UpdatedAt = now };
        db.ReleaseReadinessRecords.Add(readiness);
        AddEvent(W.WorkstreamEventNames.ReleaseReadinessChangedV1, workstream, readiness.Id, readiness.Revision, readiness.TypeKey, status.ToLowerInvariant(), new { readiness.Id, status, request.Findings });
        await db.SaveChangesAsync(token); return MapRelease(readiness);
    }

    private async Task<MutationResponse> ProposePublicationAsync(Guid organizationId, Guid installationId, Guid actorId, W.PublicationProposalRequest request, CancellationToken token)
    {
        await RequireWorkstreamAsync(organizationId, actorId, request.WorkstreamId, token);
        Text(request.ProviderKey, 1, 200, "Provider key"); Text(request.DestinationKey, 1, 200, "Destination key"); Text(request.IdempotencyKey, 1, 200, "Idempotency key");
        var readiness = await db.ReleaseReadinessRecords.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
            x.WorkstreamId == request.WorkstreamId && x.Id == request.ReleaseReadinessId && x.Status == "Ready", token)
            ?? throw new InvalidOperationException("Publication requires a Ready release-readiness record from this Workstream.");
        var existing = await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == request.IdempotencyKey, token);
        if (existing is not null) return new MutationResponse(existing.Status == ProposalStatus.Approved, 0, existing.Id, $"The publication proposal is {existing.Status}.");
        var payload = JsonSerializer.SerializeToElement(request, JsonOptions); var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ActionType = "publication.execute.v1", Summary = $"Publish {readiness.TypeKey} through {request.ProviderKey}", RiskClass = "PublicMutation",
            IdempotencyKey = request.IdempotencyKey, CreatedAt = clock.GetUtcNow(), PayloadJson = JsonSerializer.Serialize(new
            { channelId = "platform-capability", actionType = "publication.execute.v1", payloadHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                idempotencyKey = request.IdempotencyKey, resourceId = readiness.Id.ToString("D"), expectedRevision = readiness.Revision,
                alwaysRequiresApproval = true, payload }, JsonOptions) };
        db.ActionProposals.Add(proposal); await db.SaveChangesAsync(token);
        return new MutationResponse(false, readiness.Revision, proposal.Id, "Publication is awaiting explicit human approval.");
    }

    private async Task<Workstream> RequireWorkstreamAsync(Guid organizationId, Guid actorId, Guid id, CancellationToken token)
    {
        var workstream = await db.Workstreams.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, token)
            ?? throw new KeyNotFoundException("The Workstream was not found.");
        if (workstream.AccountableManagerOrganizationUserId == actorId || await db.WorkstreamSupervisionAssignments.AsNoTracking().AnyAsync(x =>
            x.WorkstreamId == id && x.SupervisorOrganizationUserId == actorId && x.EndsAt == null, token)) return workstream;
        var teams = await db.WorkstreamTeamAssignments.AsNoTracking().Where(x => x.WorkstreamId == id && x.EndsAt == null).Select(x => x.TeamId).ToListAsync(token);
        if (!await db.TeamMemberships.AsNoTracking().AnyAsync(x => teams.Contains(x.TeamId) && x.OrganizationUserId == actorId && x.EndedAt == null, token))
            throw new UnauthorizedAccessException("The Workstream is outside this employee's scope.");
        return workstream;
    }

    private void AddEvent(string type, Workstream workstream, Guid aggregateId, long revision, string typeKey, string action, object metadata,
        Guid? targetInstallationId = null)
    {
        var now = clock.GetUtcNow(); var context = new W.AgentWorkContext(workstream.OrganizationId, workstream.Id, null, null, null, null, null, Guid.NewGuid(), null, workstream.ProfileKey);
        var data = new W.GenericResourceEvent(Guid.NewGuid(), now, context, "DeliveryEvidence", aggregateId, revision, typeKey, action, JsonSerializer.SerializeToElement(metadata, JsonOptions));
        db.AgentPlatformEventOutbox.Add(new AgentPlatformEventOutboxItem { Id = Guid.NewGuid(), OrganizationId = workstream.OrganizationId,
            TargetInstallationId = targetInstallationId,
            EventType = type, DataJson = JsonSerializer.Serialize(data, JsonOptions), IdempotencyKey = $"{type}:{aggregateId:N}:{revision}",
            Status = AgentPlatformEventOutboxStatus.Pending, NextAttemptAt = now, OccurredAt = now });
    }

    private static W.ToolchainAdapterDefinition MapToolchain(ToolchainAdapterDefinitionRecord x)
    {
        using var document = JsonDocument.Parse(x.DefinitionJson);
        var root = document.RootElement;
        var recipes = root.GetProperty("recipes").EnumerateArray().Select(recipe => new W.ToolchainRecipeDefinition(
            recipe.GetProperty("key").GetString()!,
            recipe.GetProperty("operations").EnumerateArray().Select(v => v.GetString()!).ToList(),
            recipe.GetProperty("targetKeys").EnumerateArray().Select(v => v.GetString()!).ToList(),
            recipe.GetProperty("configurationSchema").Clone(),
            recipe.GetProperty("requiredEnvironmentProfileKeys").EnumerateArray().Select(v => v.GetString()!).ToList(),
            recipe.GetProperty("certificationFixtures").EnumerateArray().Select(fixture => new W.ToolchainCertificationFixture(
                fixture.GetProperty("key").GetString()!, fixture.GetProperty("resource").GetString()!,
                fixture.GetProperty("expectedCheckKeys").EnumerateArray().Select(v => v.GetString()!).ToList())).ToList())).ToList();
        return new W.ToolchainAdapterDefinition(x.Id, x.Key, x.Version, x.DisplayName, x.ProviderPackageId,
            x.ProviderPackageVersion, x.DefinitionDigest, recipes, root.GetProperty("requiredExecutableVersions").Clone(),
            root.GetProperty("outputPolicy").Clone(), root.GetProperty("supportedContentTypes").EnumerateArray().Select(v => v.GetString()!).ToList(),
            root.GetProperty("previewModes").EnumerateArray().Select(v => v.GetString()!).ToList(), x.CreatedAt);
    }
    private static W.DeliveryBuildV2 MapBuild(DeliveryBuildRecord x) => new(x.Id, x.WorkstreamId, x.TeamId,
        x.ToolchainDefinitionId, x.ProviderInstallationId, x.RepositoryId, x.SourceRevision, x.RecipeKey, x.TargetKey,
        Element(x.ConfigurationJson), x.DefinitionDigest, x.Status, x.Attempt, x.MaximumAttempts, x.ClaimId, x.ExecutionNodeId,
        x.LeaseExpiresAt, List<W.BuildOutputManifestEntry>(x.OutputsJson),
        x.ProvenanceJson == "{}" ? null : JsonSerializer.Deserialize<W.BuildExecutionProvenance>(x.ProvenanceJson, JsonOptions),
        x.FailureCode, x.FailureSummary, x.Revision, x.CreatedAt, x.UpdatedAt);
    private static W.DeliveryValidation MapValidation(DeliveryValidationRecord x) => new(x.Id, x.WorkstreamId, x.BuildId, x.TypeKey, x.Status,
        x.Summary, List<W.ReviewFinding>(x.FindingsJson), List<W.EvidenceReference>(x.EvidenceJson), x.CreatedAt, x.CompletedAt);
    private static W.DeliveryPreviewV2 MapPreview(PreviewSessionRecord x) => new(x.Id, x.WorkstreamId, x.BuildId,
        x.Mode, x.Status, x.AccessReference, List<W.EvidenceReference>(x.EvidenceJson), x.ExpiresAt, x.CreatedAt);
    private static W.EvaluationSession MapEvaluation(EvaluationSessionRecord x) => new(x.Id, x.WorkstreamId, x.BuildId, x.TypeKey, Element(x.PlanJson),
        x.ConsentPolicyKey, x.Status, Element(x.ReportJson), List<W.EvidenceReference>(x.EvidenceJson), x.Revision, x.CreatedAt, x.UpdatedAt);
    private static W.ReleaseReadiness MapRelease(ReleaseReadinessRecord x) => new(x.Id, x.WorkstreamId, x.TypeKey, x.Status,
        List<W.EvidenceReference>(x.EvidenceJson), List<W.ReviewFinding>(x.FindingsJson), x.Revision, x.CreatedAt, x.UpdatedAt);
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static IReadOnlyList<T> List<T>(string json) { try { return JsonSerializer.Deserialize<IReadOnlyList<T>>(json, JsonOptions) ?? []; } catch { return []; } }
    private static bool IsGitCommit(string value) => value.Length == 40 &&
        value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
    private static string NormalizeSha256(string value, string name)
    {
        var normalized = value.StartsWith("sha256:", StringComparison.Ordinal) ? value : $"sha256:{value}";
        if (normalized.Length != 71 || normalized.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new InvalidOperationException($"The {name} digest is invalid.");
        return normalized;
    }
    private static (string OperatingSystem, string Architecture) EnvironmentPlatform(string profileKey)
    {
        var operatingSystem = profileKey.Contains("windows", StringComparison.OrdinalIgnoreCase) ? "windows" :
            profileKey.Contains("linux", StringComparison.OrdinalIgnoreCase) ? "linux" :
            throw new InvalidOperationException("The certified environment profile does not declare a supported operating system.");
        var architecture = profileKey.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" :
            profileKey.Contains("x64", StringComparison.OrdinalIgnoreCase) ? "x64" :
            throw new InvalidOperationException("The certified environment profile does not declare a supported architecture.");
        return (operatingSystem, architecture);
    }
    private static void Text(string? value, int min, int max, string name) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < min || value.Trim().Length > max) throw new ArgumentException($"{name} is invalid."); }
    internal void RequireActiveClaim(DeliveryBuildRecord build, Guid installationId, Guid claimId, long expectedRevision)
    {
        if (build.ProviderInstallationId != installationId || build.ClaimId != claimId)
            throw new UnauthorizedAccessException("Only the installation holding this build lease may mutate execution state.");
        if (build.Revision != expectedRevision) throw new DbUpdateConcurrencyException("The build claim revision is stale.");
        if (build.LeaseExpiresAt <= clock.GetUtcNow()) throw new InvalidOperationException("The build lease expired.");
    }
    internal static void ValidateOutputs(IReadOnlyList<W.BuildOutputManifestEntry> outputs,
        JsonElement outputPolicy, IReadOnlyList<string> supportedContentTypes)
    {
        var maximumFileCount = outputPolicy.GetProperty("maximumFileCount").GetInt32();
        var maximumFileBytes = outputPolicy.GetProperty("maximumFileBytes").GetInt64();
        var maximumTotalBytes = outputPolicy.GetProperty("maximumTotalBytes").GetInt64();
        if (outputs.Count > maximumFileCount)
            throw new ArgumentException("The build output manifest exceeds the certified file-count limit.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var output in outputs)
        {
            Text(output.RelativePath, 1, 512, "Output relative path");
            var normalized = output.RelativePath.Replace('\\', '/');
            if (Path.IsPathRooted(output.RelativePath) || normalized.StartsWith('/') ||
                normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal) ||
                !paths.Add(normalized))
                throw new ArgumentException("Build output paths must be unique and remain inside the bounded output directory.");
            if (output.Size is < 0 || output.Size > maximumFileBytes)
                throw new ArgumentException("A build output exceeds the certified per-file limit.");
            totalBytes = checked(totalBytes + output.Size);
            if (totalBytes > maximumTotalBytes)
                throw new ArgumentException("The build output manifest exceeds the certified total-size limit.");
            if (output.Sha256.Length != 64 || output.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("Every build output requires a SHA-256 digest.");
            Text(output.ContentType, 1, 200, "Output content type");
            if (!supportedContentTypes.Contains(output.ContentType, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Build output content type '{output.ContentType}' is not declared by the certified adapter.");
            Text(output.TypeKey, 1, 200, "Output type key");
        }
    }
    internal static string ComputeNormalizedOutputManifestHash(IReadOnlyList<W.BuildOutputManifestEntry> outputs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var output in outputs.OrderBy(x => x.RelativePath.Replace('\\', '/'), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", output.RelativePath.Replace('\\', '/'));
                writer.WriteString("sha256", output.Sha256.ToLowerInvariant());
                writer.WriteNumber("size", output.Size);
                writer.WriteString("contentType", output.ContentType.ToLowerInvariant());
                writer.WriteString("typeKey", output.TypeKey);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions) ?? throw new JsonException("The capability payload is required.");
    private static CapabilityResult Success<T>(string id, T value) => new() { RequestId = id, Succeeded = true, ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)) };
    private static CapabilityResult Failure(string id, PlatformCapabilityErrorCode code, string message) => new() { RequestId = id, Succeeded = false, ContentType = "application/json", Error = message, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, message), JsonOptions)) };
}
