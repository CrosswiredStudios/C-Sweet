using CSweet.Office.Contracts.ControlPlane;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CSweet.Infrastructure.Setup;

public sealed class ExecutionFleetService(
    CSweetDbContext dbContext,
    IAuditEventWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<ExecutionFleetOptions>? fleetOptions = null,
    IExecutionNodeCertificateAuthority? certificateAuthority = null,
    IConfiguration? appConfiguration = null,
    IOptions<AgentRuntimeManagerOptions>? runtimeOptions = null) : IExecutionFleetService
{
    private const string CurrentProtocolVersion = "1.0";
    private static readonly Version MinimumNodeVersion = new(1, 0, 2);
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(30);
    private bool FleetEnabled => fleetOptions?.Value.PublicLaunchEnabled == true;
    private ExecutionFleetOptions FleetPolicy => fleetOptions?.Value ?? new ExecutionFleetOptions();
    private AgentRuntimeManagerOptions RuntimePolicy => runtimeOptions?.Value ?? new AgentRuntimeManagerOptions();

    public async Task EnsureDefaultPoolAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.AgentRuntimeGlobalSettings
            .OrderBy(x => x.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var pools = await dbContext.ExecutionPools.OrderBy(x => x.CreatedAt).ToListAsync(cancellationToken);
        if (pools.Count == 0)
        {
            var now = timeProvider.GetUtcNow();
            var pool = new ExecutionPool
            {
                Id = Guid.NewGuid(),
                Name = "Default",
                IsDefaultBuildPool = true,
                IsDefaultRuntimePool = true,
                IsEnabled = true,
                MaximumActiveWorkloads = 100,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.ExecutionPools.Add(pool);
            pools.Add(pool);
        }

        var enabled = pools.Where(x => x.IsEnabled).ToArray();
        if (enabled.Length == 0)
            throw new InvalidOperationException("At least one enabled execution pool is required.");
        var buildPool = pools.SingleOrDefault(x => x.IsDefaultBuildPool) ??
            pools.FirstOrDefault(x => x.Id == settings?.DefaultBuildExecutionPoolId && x.IsEnabled) ?? enabled[0];
        var runtimePool = pools.SingleOrDefault(x => x.IsDefaultRuntimePool) ??
            pools.FirstOrDefault(x => x.Id == settings?.DefaultRuntimeExecutionPoolId && x.IsEnabled) ?? buildPool;
        buildPool.IsDefaultBuildPool = true;
        runtimePool.IsDefaultRuntimePool = true;
        if (settings is not null)
        {
            settings.DefaultBuildExecutionPoolId = buildPool.Id;
            settings.DefaultRuntimeExecutionPoolId = runtimePool.Id;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ExecutionCapacityOnboardingResponse> GetOnboardingStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultPoolAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await ExpireEnrollmentsAsync(now, cancellationToken);
        var configuration = await dbContext.SystemConfigurations
            .OrderBy(x => x.CreatedAt)
            .FirstAsync(cancellationToken);
        var defaultPools = await DefaultPoolsAsync(cancellationToken);
        var pool = await dbContext.ExecutionPools.AsNoTracking()
            .SingleAsync(x => x.Id == defaultPools.EnrollmentPoolId, cancellationToken);
        var poolIds = new[] { defaultPools.BuildPoolId, defaultPools.RuntimePoolId }.Distinct().ToArray();
        var nodes = await dbContext.ExecutionNodes
            .AsNoTracking()
            .Include(x => x.Providers)
            .Where(x => poolIds.Contains(x.ExecutionPoolId))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var buildReady = FleetEnabled && ImagePolicyConfigured() && nodes.Any(node =>
            node.ExecutionPoolId == defaultPools.BuildPoolId && Qualifies(node, now, ExecutionWorkloadKind.Builder));
        var runtimeReady = FleetEnabled && ImagePolicyConfigured() && nodes.Any(node =>
            node.ExecutionPoolId == defaultPools.RuntimePoolId && Qualifies(node, now, ExecutionWorkloadKind.Runtime));
        var isReady = buildReady && runtimeReady;
        var ready = FleetEnabled && ImagePolicyConfigured()
            ? nodes.Where(node =>
                node.ExecutionPoolId == defaultPools.BuildPoolId && Qualifies(node, now, ExecutionWorkloadKind.Builder) ||
                node.ExecutionPoolId == defaultPools.RuntimePoolId && Qualifies(node, now, ExecutionWorkloadKind.Runtime)).ToArray()
            : [];
        var pending = nodes.Count(node => node.Status == ExecutionNodeStatus.PendingApproval);
        var activeEnrollment = await dbContext.ExecutionNodeEnrollments
            .AsNoTracking()
            .Where(x => x.ExecutionPoolId == pool.Id &&
                (x.Status == ExecutionEnrollmentStatus.Available || x.Status == ExecutionEnrollmentStatus.Claimed) &&
                x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        const string mode = "remote";
        var checks = Checks(ExecutionOnboardingMode.Remote, nodes, isReady, ready.Length, activeEnrollment).ToList();
        checks.Insert(0, FleetEnabled
            ? Passed("launch-gate", "Execution fleet release gate", "Production platform certification gate is enabled.")
            : Required("launch-gate", "Execution fleet release gate",
                "The execution fleet is not enabled for this release.",
                "Enable the fleet only after Windows, Linux, and macOS builder/runtime certification passes."));
        checks.Insert(1, ImagePolicyConfigured()
            ? Passed("image-policy", "Signed guest image policy", FleetPolicy.AllowUnpinnedDevelopmentImages
                ? "Development AppHost permits dynamically generated certified image variants."
                : "Exact builder and runtime image variants are pinned for this deployment.")
            : Required("image-policy", "Signed guest image policy",
                "Required builder and runtime guest image digests are not configured.",
                "Configure both CSweet:AgentRuntime image digests before enabling production execution."));
        IReadOnlyList<ExecutionCapacityCheckResponse> localPrerequisites = [];
        return new ExecutionCapacityOnboardingResponse(
            mode,
            isReady,
            ready.Length,
            pending,
            false,
            LocalOperatingSystem(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            !FleetEnabled
                ? "Distributed execution is gated until every supported platform has production builder/runtime certification."
                : isReady
                ? $"{ready.Length} approved Office{(ready.Length == 1 ? " is" : "s are")} ready for agent builds and runtimes."
                : "Install, connect, and approve at least one certified Office.",
            activeEnrollment is null ? null : Map(activeEnrollment),
            nodes.Select(Map).ToArray(),
            checks,
            localPrerequisites,
            new OfficePackageLinksResponse(
                fleetOptions?.Value.ReleaseManifestUrl ??
                    "https://github.com/CrosswiredStudios/CSweet.Office/releases/latest/download/office-release.json",
                fleetOptions?.Value.WindowsPackageOverrideUrl,
                fleetOptions?.Value.LinuxPackageOverrideUrl,
                fleetOptions?.Value.MacOsPackageOverrideUrl,
                PublicControlPlaneUrl(appConfiguration)),
            null);
    }

    public async Task<ExecutionCapacityActionResponse> SelectOnboardingModeAsync(
        SelectExecutionOnboardingModeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Mode, "remote", StringComparison.OrdinalIgnoreCase))
            return await FailureAsync("invalid_mode", "Use the Office workflow for this or another machine.", cancellationToken);
        const ExecutionOnboardingMode mode = ExecutionOnboardingMode.Remote;

        var configuration = await dbContext.SystemConfigurations
            .OrderBy(x => x.CreatedAt)
            .FirstAsync(cancellationToken);
        configuration.ExecutionOnboardingMode = mode;
        configuration.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "setup.execution.mode.selected",
            nameof(SystemConfiguration),
            configuration.Id,
            $"Selected {mode} execution-node onboarding.",
            cancellationToken: cancellationToken);
        return await SuccessAsync($"Selected {mode.ToString().ToLowerInvariant()} execution-node onboarding.", cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> CreateEnrollmentAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultPoolAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await ExpireEnrollmentsAsync(now, cancellationToken);
        var pool = await DefaultPoolAsync(cancellationToken);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var enrollment = new ExecutionNodeEnrollment
        {
            Id = Guid.NewGuid(),
            ExecutionPoolId = pool.Id,
            TokenHash = Hash(token),
            ReceiptHash = Hash(Base64Url(RandomNumberGenerator.GetBytes(32))),
            Status = ExecutionEnrollmentStatus.Available,
            ExpiresAt = now.Add(EnrollmentLifetime),
            CreatedAt = now
        };
        dbContext.ExecutionNodeEnrollments.Add(enrollment);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "execution-node.enrollment.created",
            nameof(ExecutionNodeEnrollment), enrollment.Id,
            $"Created a one-use execution-node enrollment for pool {pool.Id}.",
            cancellationToken: cancellationToken);
        var status = await GetOnboardingStatusAsync(cancellationToken);
        var response = Map(enrollment) with { EnrollmentToken = token };
        return new ExecutionCapacityActionResponse(
            true, null, "Enrollment created. The token is shown only once.", status, response);
    }

    public async Task<ExecutionCapacityActionResponse> RevokeEnrollmentAsync(
        Guid enrollmentId,
        CancellationToken cancellationToken = default)
    {
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleOrDefaultAsync(x => x.Id == enrollmentId, cancellationToken);
        if (enrollment is null)
            return await FailureAsync("enrollment_not_found", "The enrollment was not found.", cancellationToken);
        if (enrollment.Status != ExecutionEnrollmentStatus.Available)
            return await FailureAsync("enrollment_not_revocable", "Only an unused enrollment can be revoked.", cancellationToken);
        enrollment.Status = ExecutionEnrollmentStatus.Revoked;
        enrollment.RevokedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "execution-node.enrollment.revoked",
            nameof(ExecutionNodeEnrollment), enrollment.Id,
            "Revoked an unused execution-node enrollment.",
            cancellationToken: cancellationToken);
        return await SuccessAsync("Enrollment revoked.", cancellationToken);
    }

    public async Task<ClaimOfficeResponse> ClaimNodeAsync(
        ClaimOfficeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { ValidateClaim(request); }
        catch (ArgumentException)
        {
            return new ClaimOfficeResponse(
                false, "invalid_node_claim", "The execution-node enrollment claim is invalid.", null, null);
        }
        var now = timeProvider.GetUtcNow();
        var tokenHash = Hash(request.EnrollmentToken);
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (enrollment is null || enrollment.Status != ExecutionEnrollmentStatus.Available || enrollment.ExpiresAt <= now)
            return new ClaimOfficeResponse(false, "invalid_enrollment", "The enrollment is invalid, expired, or already used.", null, null);

        var receipt = Base64Url(RandomNumberGenerator.GetBytes(32));
        var node = new ExecutionNode
        {
            Id = Guid.NewGuid(),
            ExecutionPoolId = enrollment.ExecutionPoolId,
            Name = request.Name.Trim(),
            MachineName = request.MachineName.Trim(),
            OperatingSystem = request.OperatingSystem.Trim().ToLowerInvariant(),
            Architecture = request.Architecture.Trim().ToLowerInvariant(),
            NodeVersion = request.OfficeVersion.Trim(),
            ProtocolVersion = request.ProtocolVersion.Trim(),
            Status = ExecutionNodeStatus.PendingApproval,
            CertificateThumbprint = NormalizeHex(request.CertificateThumbprint),
            CertificateSerialNumber = NormalizeHex(request.CertificateSerialNumber),
            CertificateExpiresAt = request.CertificateExpiresAt.ToUniversalTime(),
            CertificateSigningRequestPem = request.CertificateSigningRequestPem.Trim(),
            LabelsJson = SecurityPostureLabels(request.SecurityPosture),
            AllocatableCpuCount = request.AllocatableCpuCount,
            AllocatableMemoryMb = request.AllocatableMemoryMb,
            AllocatableDiskMb = request.AllocatableDiskMb,
            MaximumConcurrentWorkloads = request.MaximumConcurrentWorkloads,
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var provider in request.Providers)
            node.Providers.Add(Map(node.Id, provider, now));
        enrollment.ExecutionNodeId = node.Id;
        enrollment.ReceiptHash = Hash(receipt);
        enrollment.Status = ExecutionEnrollmentStatus.Claimed;
        enrollment.ClaimedAt = now;
        dbContext.ExecutionNodes.Add(node);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "execution-node.enrollment.claimed",
            nameof(ExecutionNode), node.Id,
            $"Office {node.Name} claimed enrollment {enrollment.Id} and is pending approval.",
            cancellationToken: cancellationToken);
        return new ClaimOfficeResponse(true, null, "Office enrolled and awaiting administrator approval.", node.Id, receipt);
    }

    public async Task<ExecutionCapacityActionResponse> ApproveNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var node = await dbContext.ExecutionNodes
            .Include(x => x.Providers)
            .SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is null)
            return await FailureAsync("node_not_found", "The Office was not found.", cancellationToken);
        if (node.Status != ExecutionNodeStatus.PendingApproval)
            return await FailureAsync("node_not_pending", "Only pending Offices can be approved.", cancellationToken);
        if (QualificationFailure(node, now) is { } qualificationFailure)
            return await FailureAsync("node_not_qualified", qualificationFailure, cancellationToken);

        IssuedExecutionNodeCertificate issued;
        try
        {
            issued = certificateAuthority?.Issue(node.CertificateSigningRequestPem, node.Id)
                ?? throw new InvalidOperationException("The execution-node certificate authority is unavailable.");
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or CryptographicException)
        {
            return await FailureAsync(
                "node_certificate_rejected",
                "The node certificate request could not be approved.", cancellationToken);
        }

        node.Status = ExecutionNodeStatus.Ready;
        node.CertificateThumbprint = NormalizeHex(issued.Thumbprint);
        node.CertificateSerialNumber = NormalizeHex(issued.SerialNumber);
        node.CertificateExpiresAt = issued.ExpiresAt.ToUniversalTime();
        node.IssuedCertificateBase64 = issued.CertificateBase64;
        node.LastHeartbeatAt = null;
        node.ApprovedAt = now;
        node.UpdatedAt = now;
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleAsync(x => x.ExecutionNodeId == node.Id, cancellationToken);
        enrollment.Status = ExecutionEnrollmentStatus.Approved;
        enrollment.ApprovedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "execution-node.approved",
            nameof(ExecutionNode), node.Id,
            $"Approved Office {node.Name} for pool {node.ExecutionPoolId}.",
            cancellationToken: cancellationToken);
        return await SuccessAsync("Office approved; waiting for its authenticated control connection.", cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> RejectNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var node = await dbContext.ExecutionNodes
            .SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is null)
            return await FailureAsync("node_not_found", "The Office was not found.", cancellationToken);
        if (node.Status != ExecutionNodeStatus.PendingApproval)
            return await FailureAsync("node_not_pending", "Only a pending Office can be rejected.", cancellationToken);
        var now = timeProvider.GetUtcNow();
        node.Status = ExecutionNodeStatus.Revoked;
        node.RevokedAt = now;
        node.UpdatedAt = now;
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleAsync(x => x.ExecutionNodeId == nodeId, cancellationToken);
        enrollment.Status = ExecutionEnrollmentStatus.Revoked;
        enrollment.RevokedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "execution-node.rejected", nameof(ExecutionNode), node.Id,
            $"Rejected pending Office {node.Name}.", cancellationToken: cancellationToken);
        return await SuccessAsync("Office rejected.", cancellationToken);
    }

    public async Task<bool> RecordHeartbeatAsync(
        Guid nodeId,
        OfficeHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        var receiptHash = Hash(request.EnrollmentReceipt);
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleOrDefaultAsync(x => x.ExecutionNodeId == nodeId && x.ReceiptHash == receiptHash,
                cancellationToken);
        if (enrollment is null || enrollment.Status != ExecutionEnrollmentStatus.Claimed)
            return false;
        var node = await dbContext.ExecutionNodes.Include(x => x.Providers)
            .SingleAsync(x => x.Id == nodeId, cancellationToken);
        if (node.Status == ExecutionNodeStatus.Revoked || request.SessionEpoch < node.SessionEpoch)
            return false;

        node.SessionEpoch = request.SessionEpoch;
        node.AllocatableCpuCount = request.AllocatableCpuCount;
        node.AllocatableMemoryMb = request.AllocatableMemoryMb;
        node.AllocatableDiskMb = request.AllocatableDiskMb;
        node.MaximumConcurrentWorkloads = request.MaximumConcurrentWorkloads;
        node.LabelsJson = SecurityPostureLabels(request.SecurityPosture, node.LabelsJson);
        node.LastHeartbeatAt = now;
        node.UpdatedAt = now;
        SynchronizeProviderInventory(node, request.Providers, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<OfficeCertificateResponse> GetOperationalCertificateAsync(
        Guid nodeId,
        OfficeCertificateRequest request,
        CancellationToken cancellationToken = default)
    {
        var receiptHash = Hash(request.EnrollmentReceipt);
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleOrDefaultAsync(x => x.ExecutionNodeId == nodeId && x.ReceiptHash == receiptHash,
                cancellationToken);
        if (enrollment?.Status != ExecutionEnrollmentStatus.Approved)
            return new(false, "certificate_not_available", "The node is not approved.", null, null, null);
        var node = await dbContext.ExecutionNodes.SingleAsync(x => x.Id == nodeId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (node.Status == ExecutionNodeStatus.Revoked)
            return new(false, "node_revoked", "The node is revoked.", null, null, null);
        if (!TryIssueCertificate(node, now, out var error))
            return error!;
        await dbContext.SaveChangesAsync(cancellationToken);
        return CertificateResponse(node, "Operational certificate issued; bootstrap completes on the first authenticated control heartbeat.");
    }

    public async Task<OfficeCertificateResponse> RotateOperationalCertificateAsync(
        Guid nodeId,
        string certificateThumbprint,
        string certificateSerialNumber,
        CancellationToken cancellationToken = default)
    {
        var node = await dbContext.ExecutionNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (node is null || node.Status == ExecutionNodeStatus.Revoked || node.ApprovedAt is null ||
            !string.Equals(NormalizeHex(certificateThumbprint), node.CertificateThumbprint, StringComparison.Ordinal) ||
            !string.Equals(NormalizeHex(certificateSerialNumber), node.CertificateSerialNumber, StringComparison.Ordinal) ||
            node.CertificateExpiresAt <= now)
            return new(false, "node_certificate_rejected", "A current operational node certificate is required.", null, null, null);
        if (!TryIssueCertificate(node, now, out var error))
            return error!;
        await dbContext.SaveChangesAsync(cancellationToken);
        return CertificateResponse(node, "Operational certificate is current.");
    }

    private bool TryIssueCertificate(
        ExecutionNode node,
        DateTimeOffset now,
        out OfficeCertificateResponse? error)
    {
        error = null;
        if (node.Status == ExecutionNodeStatus.Revoked)
        {
            error = new(false, "node_revoked", "The node is revoked.", null, null, null);
            return false;
        }
        if (node.CertificateExpiresAt > now.AddHours(6) && !string.IsNullOrWhiteSpace(node.IssuedCertificateBase64))
            return true;
        try
        {
            var issued = certificateAuthority?.Issue(node.CertificateSigningRequestPem, node.Id)
                ?? throw new InvalidOperationException("The execution-node certificate authority is unavailable.");
            node.CertificateThumbprint = NormalizeHex(issued.Thumbprint);
            node.CertificateSerialNumber = NormalizeHex(issued.SerialNumber);
            node.CertificateExpiresAt = issued.ExpiresAt.ToUniversalTime();
            node.IssuedCertificateBase64 = issued.CertificateBase64;
            node.UpdatedAt = now;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or CryptographicException)
        {
            error = new(false, "certificate_rotation_failed", "The operational certificate could not be issued.", null, null, null);
            return false;
        }
    }

    private static OfficeCertificateResponse CertificateResponse(ExecutionNode node, string message) =>
        new(true, null, message, node.IssuedCertificateBase64,
            node.CertificateThumbprint, node.CertificateExpiresAt);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!FleetEnabled || !ImagePolicyConfigured()) return false;
        await EnsureDefaultPoolAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var defaults = await DefaultPoolsAsync(cancellationToken);
        var nodes = await dbContext.ExecutionNodes.AsNoTracking()
            .Include(node => node.Providers)
            .Where(node => node.ExecutionPoolId == defaults.BuildPoolId ||
                node.ExecutionPoolId == defaults.RuntimePoolId)
            .ToListAsync(cancellationToken);
        return nodes.Any(node => node.ExecutionPoolId == defaults.BuildPoolId &&
                Qualifies(node, now, ExecutionWorkloadKind.Builder)) &&
            nodes.Any(node => node.ExecutionPoolId == defaults.RuntimePoolId &&
                Qualifies(node, now, ExecutionWorkloadKind.Runtime));
    }

    private async Task<DefaultPools> DefaultPoolsAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.AgentRuntimeGlobalSettings.AsNoTracking()
            .OrderBy(x => x.UpdatedAt).FirstAsync(cancellationToken);
        var buildPoolId = settings.DefaultBuildExecutionPoolId ??
            await dbContext.ExecutionPools.Where(x => x.IsDefaultBuildPool).Select(x => x.Id).SingleAsync(cancellationToken);
        var runtimePoolId = settings.DefaultRuntimeExecutionPoolId ??
            await dbContext.ExecutionPools.Where(x => x.IsDefaultRuntimePool).Select(x => x.Id).SingleAsync(cancellationToken);
        var combined = await dbContext.ExecutionPools.AsNoTracking()
            .Where(x => x.IsDefaultBuildPool && x.IsDefaultRuntimePool && x.IsEnabled)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        return new DefaultPools(buildPoolId, runtimePoolId, combined ?? runtimePoolId);
    }

    private async Task<ExecutionPool> DefaultPoolAsync(CancellationToken cancellationToken)
    {
        var defaults = await DefaultPoolsAsync(cancellationToken);
        return await dbContext.ExecutionPools.SingleAsync(x => x.Id == defaults.EnrollmentPoolId, cancellationToken);
    }

    private async Task ExpireEnrollmentsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await dbContext.ExecutionNodeEnrollments
            .Where(x => x.ExpiresAt <= now &&
                x.Status == ExecutionEnrollmentStatus.Available)
            .ToListAsync(cancellationToken);
        foreach (var enrollment in expired)
            enrollment.Status = ExecutionEnrollmentStatus.Expired;
        if (expired.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private bool Qualifies(ExecutionNode node, DateTimeOffset now, ExecutionWorkloadKind kind) =>
        node.Status == ExecutionNodeStatus.Ready &&
        node.ApprovedAt is not null && node.DrainingAt is null && node.RevokedAt is null &&
        node.LastHeartbeatAt >= now.Subtract(HeartbeatFreshness) &&
        ProviderQualifies(node, now, kind) && CapacityQualifies(node, kind);

    private bool IdentityAndProvidersQualify(ExecutionNode node, DateTimeOffset now) =>
        QualificationFailure(node, now) is null;

    private string? QualificationFailure(ExecutionNode node, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(node.CertificateThumbprint) || node.CertificateExpiresAt <= now)
            return "The Office identity certificate is missing or expired. Re-enroll this host.";
        if (!string.Equals(node.ProtocolVersion, CurrentProtocolVersion, StringComparison.Ordinal))
            return $"The Office protocol version is not supported. Required: {CurrentProtocolVersion}; reported: {node.ProtocolVersion}.";
        if (!Version.TryParse(node.NodeVersion, out var version) || version < MinimumNodeVersion)
            return $"The Office version is not supported. Install version {MinimumNodeVersion} or later.";
        if (!ImagePolicyConfigured())
            return "Headquarters has no permitted builder/runtime guest-image policy. Configure pinned image digests, or enable certified unpinned images for development.";
        if (node.Providers.Count == 0)
            return "The Office did not report an isolation provider. Check the RuntimeHost service and provider configuration.";
        if (node.Providers.Any(provider =>
                ProviderQualifies(provider, now, ExecutionWorkloadKind.Builder) &&
                ProviderQualifies(provider, now, ExecutionWorkloadKind.Runtime)))
            return null;

        var unavailable = node.Providers.FirstOrDefault(provider => !provider.IsAvailable);
        if (unavailable is not null)
            return $"Provider {unavailable.ProviderId} is unavailable: {ProviderDiagnostic(unavailable.UnavailableReason)}";
        var wrongSuite = node.Providers.FirstOrDefault(provider =>
            !string.Equals(provider.CertificationSuiteVersion, RuntimePolicy.RequiredCertificationSuiteVersion, StringComparison.Ordinal));
        if (wrongSuite is not null)
            return $"Provider {wrongSuite.ProviderId} has certification suite {wrongSuite.CertificationSuiteVersion}; headquarters requires {RuntimePolicy.RequiredCertificationSuiteVersion}.";
        var expired = node.Providers.FirstOrDefault(provider =>
            provider.CertifiedAt > now || provider.CertificationExpiresAt <= now);
        if (expired is not null)
            return $"Provider {expired.ProviderId} certification is not currently valid. Rebuild and recertify the Office payload.";
        var missingCapability = node.Providers.FirstOrDefault(provider =>
            !provider.SupportsBuilderWorkloads || !provider.SupportsRuntimeWorkloads);
        if (missingCapability is not null)
            return $"Provider {missingCapability.ProviderId} does not report both builder and runtime workload support.";
        return "The reported provider does not match the required broker protocol, signed image, or certification evidence policy.";
    }

    private static string ProviderDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "no diagnostic was reported";
        if (value.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("<Objs Version=", StringComparison.OrdinalIgnoreCase))
            return "the runtime readiness check failed; review the Office RuntimeHost event log";
        var normalized = string.Join(' ', value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized[..Math.Min(512, normalized.Length)];
    }

    private bool ProviderQualifies(ExecutionNode node, DateTimeOffset now, ExecutionWorkloadKind kind) =>
        !string.IsNullOrWhiteSpace(node.CertificateThumbprint) && node.CertificateExpiresAt > now &&
        string.Equals(node.ProtocolVersion, CurrentProtocolVersion, StringComparison.Ordinal) &&
        Version.TryParse(node.NodeVersion, out var version) && version >= MinimumNodeVersion &&
        node.Providers.Any(provider => ProviderQualifies(provider, now, kind));

    private bool ProviderQualifies(ExecutionNodeProvider provider, DateTimeOffset now, ExecutionWorkloadKind kind)
    {
        var expectedDigest = kind == ExecutionWorkloadKind.Builder
            ? RuntimePolicy.BuilderGuestImageDigest : RuntimePolicy.RuntimeGuestImageDigest;
        return provider.IsAvailable &&
            (kind == ExecutionWorkloadKind.Builder ? provider.SupportsBuilderWorkloads : provider.SupportsRuntimeWorkloads) &&
            string.Equals(provider.BrokerProtocolVersion, CurrentProtocolVersion, StringComparison.Ordinal) &&
            string.Equals(provider.CertificationSuiteVersion, RuntimePolicy.RequiredCertificationSuiteVersion, StringComparison.Ordinal) &&
            provider.CertifiedAt <= now &&
            (provider.CertificationExpiresAt is null || provider.CertificationExpiresAt > now) &&
            IsSha256(provider.GuestImageDigest) && IsSha256(provider.CertificationEvidenceDigest) &&
            (FleetPolicy.AllowUnpinnedDevelopmentImages ||
                string.Equals(provider.GuestImageDigest, NormalizeDigest(expectedDigest), StringComparison.Ordinal));
    }

    private bool CapacityQualifies(ExecutionNode node, ExecutionWorkloadKind kind) =>
        node.MaximumConcurrentWorkloads > 0 && (kind == ExecutionWorkloadKind.Builder
            ? node.AllocatableCpuCount >= Math.Max(1, FleetPolicy.MinimumBuilderCpuCount) &&
              node.AllocatableMemoryMb >= Math.Max(128, FleetPolicy.MinimumBuilderMemoryMb) &&
              node.AllocatableDiskMb >= Math.Max(64, FleetPolicy.MinimumBuilderDiskMb)
            : node.AllocatableCpuCount >= Math.Max(1, FleetPolicy.MinimumRuntimeCpuCount) &&
              node.AllocatableMemoryMb >= Math.Max(128, FleetPolicy.MinimumRuntimeMemoryMb) &&
              node.AllocatableDiskMb >= Math.Max(64, FleetPolicy.MinimumRuntimeDiskMb));

    private bool ImagePolicyConfigured() => FleetPolicy.AllowUnpinnedDevelopmentImages ||
        IsSha256(NormalizeDigest(RuntimePolicy.BuilderGuestImageDigest)) &&
        IsSha256(NormalizeDigest(RuntimePolicy.RuntimeGuestImageDigest));

    private static IReadOnlyList<ExecutionCapacityCheckResponse> Checks(
        ExecutionOnboardingMode mode,
        IReadOnlyList<ExecutionNode> nodes,
        bool isReady,
        int readyCount,
        ExecutionNodeEnrollment? enrollment)
    {
        var result = new List<ExecutionCapacityCheckResponse>
        {
            mode == ExecutionOnboardingMode.None
                ? Required("mode", "Execution location", "Choose whether agents run on this machine or another machine.", "Select an execution location to continue.")
                : Passed("mode", "Execution location", $"Selected {mode.ToString().ToLowerInvariant()} execution-node onboarding."),
            nodes.Count == 0
                ? Required("node", "Office", "No Office has enrolled.",
                    enrollment is null ? "Create an enrollment and install C-Sweet Office." : "Use the one-time enrollment on an Office machine.")
                : Passed("node", "Office", $"Detected {nodes.Count} enrolled Office{(nodes.Count == 1 ? string.Empty : "s")}.")
        };
        var pending = nodes.Count(x => x.Status == ExecutionNodeStatus.PendingApproval);
        result.Add(pending > 0
            ? Required("approval", "Administrator approval", $"{pending} Office{(pending == 1 ? " is" : "s are")} awaiting approval.", "Review the Office identity and approve it.")
            : isReady
                ? Passed("approval", "Administrator approval", "A qualifying Office is approved.")
                : Required("approval", "Administrator approval", "No qualifying Office is approved.", "Enroll and approve an Office."));
        result.Add(isReady
            ? Passed("capacity", "Certified execution capacity", $"{readyCount} Office{(readyCount == 1 ? " is" : "s are")} ready for builds and runtimes.")
            : Required("capacity", "Certified execution capacity", "No approved, connected Office currently has certified builder and runtime capacity.",
                "Connect a compatible Office with current certification, images, identity, and allocatable resources."));
        return result;
    }

    private static ExecutionCapacityCheckResponse Passed(string key, string name, string message) =>
        new(key, name, "passed", message);

    private static ExecutionCapacityCheckResponse Required(string key, string name, string message, string remediation) =>
        new(key, name, "action-required", message, remediation);

    private static string? PublicControlPlaneUrl(IConfiguration? configuration)
    {
        var configured = configuration?["CSweet:ExecutionGateway:PublicUrl"]?.Trim().TrimEnd('/');
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri.TrimEnd('/')
            : null;
    }

    private static ExecutionNodeSummaryResponse Map(ExecutionNode node) => new(
        node.Id, node.ExecutionPoolId, node.Name, node.MachineName, node.OperatingSystem,
        node.Architecture, node.NodeVersion, node.ProtocolVersion,
        node.Status.ToString().ToLowerInvariant(), node.CertificateThumbprint,
        node.CertificateExpiresAt, node.AllocatableCpuCount, node.AllocatableMemoryMb,
        node.AllocatableDiskMb, node.MaximumConcurrentWorkloads, node.LastHeartbeatAt,
        node.Providers.Select(provider => new ExecutionNodeProviderResponse(
            provider.ProviderId, provider.ProviderVersion, provider.BrokerProtocolVersion,
            provider.GuestImageDigest, provider.CertificationSuiteVersion,
            provider.CertificationEvidenceDigest, provider.CertifiedAt, provider.CertificationExpiresAt,
            provider.SupportsBuilderWorkloads, provider.SupportsRuntimeWorkloads,
            provider.IsAvailable, provider.UnavailableReason)).ToArray(),
        DeserializeLabels(node.LabelsJson),
        IsLocalMachine(node.MachineName, node.OperatingSystem, Environment.MachineName, LocalOperatingSystem()));

    internal static bool IsLocalMachine(
        string machineName,
        string operatingSystem,
        string localMachineName,
        string localOperatingSystem) =>
        string.Equals(machineName, localMachineName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(operatingSystem, localOperatingSystem, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> DeserializeLabels(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    private static string SecurityPostureLabels(
        OfficeSecurityPostureReport? posture,
        string existingJson = "{}")
    {
        var labels = DeserializeLabels(existingJson).ToDictionary(item => item.Key, item => item.Value,
            StringComparer.Ordinal);
        foreach (var key in labels.Keys.Where(key => key.StartsWith("csweet.security.", StringComparison.Ordinal)).ToArray())
            labels.Remove(key);
        if (posture is null) return JsonSerializer.Serialize(labels);
        var profile = posture.Profile.Trim().ToLowerInvariant();
        if (profile is not ("baseline" or "hardened" or "development") ||
            posture.EvaluatedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
            posture.EnabledControls.Count > 64 || posture.MissingControls.Count > 64 ||
            profile == "development" && !posture.DevelopmentAssignmentsAllowed ||
            profile == "hardened" && posture.MissingControls.Count != 0)
            throw new ArgumentException("The Office security posture report is invalid.");
        labels["csweet.security.profile"] = profile;
        labels["csweet.security.mixed-use"] = posture.MixedUseHost ? "true" : "false";
        labels["csweet.security.development-assignments"] = posture.DevelopmentAssignmentsAllowed ? "true" : "false";
        labels["csweet.security.missing-controls"] = posture.MissingControls.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(labels);
    }

    private static ExecutionEnrollmentResponse Map(ExecutionNodeEnrollment enrollment) => new(
        enrollment.Id, enrollment.ExecutionPoolId, enrollment.Status.ToString().ToLowerInvariant(),
        enrollment.ExpiresAt);

    private static ExecutionNodeProvider Map(
        Guid nodeId,
        RegisterOfficeProviderRequest provider,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ExecutionNodeId = nodeId,
        ProviderId = provider.ProviderId.Trim(),
        ProviderVersion = provider.ProviderVersion.Trim(),
        BrokerProtocolVersion = provider.BrokerProtocolVersion.Trim(),
        GuestImageDigest = provider.GuestImageDigest.Trim().ToLowerInvariant(),
        CertificationSuiteVersion = provider.CertificationSuiteVersion.Trim(),
        CertificationEvidenceDigest = provider.CertificationEvidenceDigest.Trim().ToLowerInvariant(),
        CertifiedAt = provider.CertifiedAt.ToUniversalTime(),
        CertificationExpiresAt = provider.CertificationExpiresAt?.ToUniversalTime(),
        SupportsBuilderWorkloads = provider.SupportsBuilderWorkloads,
        SupportsRuntimeWorkloads = provider.SupportsRuntimeWorkloads,
        IsAvailable = provider.IsAvailable,
        UnavailableReason = string.IsNullOrWhiteSpace(provider.UnavailableReason)
            ? null : provider.UnavailableReason.Trim()[..Math.Min(1024, provider.UnavailableReason.Trim().Length)],
        UpdatedAt = now
    };

    private void SynchronizeProviderInventory(
        ExecutionNode node,
        IReadOnlyList<RegisterOfficeProviderRequest> reportedProviders,
        DateTimeOffset now)
    {
        var reportedKeys = reportedProviders
            .Select(ProviderKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in node.Providers.Where(provider =>
                     !reportedKeys.Contains(ProviderKey(provider))).ToArray())
        {
            dbContext.ExecutionNodeProviders.Remove(obsolete);
            node.Providers.Remove(obsolete);
        }

        foreach (var reported in reportedProviders)
        {
            var existing = node.Providers.SingleOrDefault(provider =>
                string.Equals(ProviderKey(provider), ProviderKey(reported), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                node.Providers.Add(Map(node.Id, reported, now));
                continue;
            }

            existing.ProviderVersion = reported.ProviderVersion.Trim();
            existing.BrokerProtocolVersion = reported.BrokerProtocolVersion.Trim();
            existing.CertificationSuiteVersion = reported.CertificationSuiteVersion.Trim();
            existing.CertificationEvidenceDigest = reported.CertificationEvidenceDigest.Trim().ToLowerInvariant();
            existing.CertifiedAt = reported.CertifiedAt.ToUniversalTime();
            existing.CertificationExpiresAt = reported.CertificationExpiresAt?.ToUniversalTime();
            existing.SupportsBuilderWorkloads = reported.SupportsBuilderWorkloads;
            existing.SupportsRuntimeWorkloads = reported.SupportsRuntimeWorkloads;
            existing.IsAvailable = reported.IsAvailable;
            existing.UnavailableReason = string.IsNullOrWhiteSpace(reported.UnavailableReason)
                ? null : reported.UnavailableReason.Trim()[..Math.Min(1024, reported.UnavailableReason.Trim().Length)];
            existing.UpdatedAt = now;
        }
    }

    private static string ProviderKey(ExecutionNodeProvider provider) =>
        $"{provider.ProviderId}\n{provider.GuestImageDigest}";

    private static string ProviderKey(RegisterOfficeProviderRequest provider) =>
        $"{provider.ProviderId.Trim()}\n{provider.GuestImageDigest.Trim()}";

    private async Task<ExecutionCapacityActionResponse> SuccessAsync(string message, CancellationToken cancellationToken) =>
        new(true, null, message, await GetOnboardingStatusAsync(cancellationToken));

    private async Task<ExecutionCapacityActionResponse> FailureAsync(string code, string message, CancellationToken cancellationToken) =>
        new(false, code, message, await GetOnboardingStatusAsync(cancellationToken));

    private static void ValidateClaim(ClaimOfficeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || request.EnrollmentToken.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 ||
            string.IsNullOrWhiteSpace(request.MachineName) || request.MachineName.Length > 255 ||
            string.IsNullOrWhiteSpace(request.OperatingSystem) || request.OperatingSystem.Length > 32 ||
            string.IsNullOrWhiteSpace(request.Architecture) || request.Architecture.Length > 32 ||
            !Version.TryParse(request.OfficeVersion, out _) || request.ProtocolVersion != CurrentProtocolVersion ||
            request.AllocatableCpuCount < 1 || request.AllocatableMemoryMb < 128 ||
            request.AllocatableDiskMb < 64 || request.MaximumConcurrentWorkloads < 1 ||
            request.CertificateExpiresAt <= DateTimeOffset.UtcNow ||
            request.CertificateSigningRequestPem.Length is < 100 or > 16 * 1024 ||
            !request.CertificateSigningRequestPem.Contains("BEGIN CERTIFICATE REQUEST", StringComparison.Ordinal) ||
            request.Providers.Count is < 1 or > 16)
            throw new ArgumentException("The execution-node enrollment request is invalid.", nameof(request));
        if (request.Providers.Any(provider =>
                string.IsNullOrWhiteSpace(provider.ProviderId) || provider.ProviderId.Length > 100 ||
                provider.ProviderVersion.Length > 64 || provider.BrokerProtocolVersion.Length > 32 ||
                provider.CertificationSuiteVersion.Length > 128 ||
                provider.UnavailableReason?.Length > 1024 ||
                provider.IsAvailable && (!IsSha256(provider.GuestImageDigest) ||
                    !IsSha256(provider.CertificationEvidenceDigest) ||
                    provider.BrokerProtocolVersion != CurrentProtocolVersion ||
                    string.IsNullOrWhiteSpace(provider.CertificationSuiteVersion))))
            throw new ArgumentException("The execution-node provider inventory is invalid.", nameof(request));
    }

    private static bool IsSupportedLocalPlatform() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static string LocalOperatingSystem() => OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unsupported";

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeHex(string value) =>
        new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());

    private static string NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("sha256:", StringComparison.Ordinal) ? normalized : $"sha256:{normalized}";
    }

    private static bool IsSha256(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private sealed record DefaultPools(Guid BuildPoolId, Guid RuntimePoolId, Guid EnrollmentPoolId);
}
