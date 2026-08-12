using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.AgentRuntime.HyperV;
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
    ILocalExecutionNodeProvisioner? localProvisioner = null,
    IOptions<AgentRuntimeManagerOptions>? runtimeOptions = null,
    IWindowsHyperVHostProbe? windowsHostProbe = null) : IExecutionFleetService
{
    private const string CurrentProtocolVersion = "1.0";
    private static readonly Version MinimumNodeVersion = new(1, 0, 0);
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

        var mode = configuration.ExecutionOnboardingMode.ToString().ToLowerInvariant();
        var checks = Checks(configuration.ExecutionOnboardingMode, nodes, isReady, ready.Length, activeEnrollment).ToList();
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
        var localProgress = localProvisioner?.GetProgress();
        var localPrerequisites = await LocalPrerequisiteChecksAsync(
            nodes, localProgress, now, cancellationToken);
        return new ExecutionCapacityOnboardingResponse(
            mode,
            isReady,
            ready.Length,
            pending,
            FleetEnabled && IsSupportedLocalPlatform(),
            LocalOperatingSystem(),
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            !FleetEnabled
                ? "Distributed execution is gated until every supported platform has production builder/runtime certification."
                : isReady
                ? $"{ready.Length} approved execution node{(ready.Length == 1 ? " is" : "s are")} ready for agent builds and runtimes."
                : "Choose where agents will run, then connect and approve at least one certified execution node.",
            activeEnrollment is null ? null : Map(activeEnrollment),
            nodes.Select(Map).ToArray(),
            checks,
            localPrerequisites,
            new ExecutionNodePackageLinksResponse(
                fleetOptions?.Value.WindowsPackageUrl,
                fleetOptions?.Value.LinuxPackageUrl,
                fleetOptions?.Value.MacOsPackageUrl,
                PublicControlPlaneUrl(appConfiguration)),
            localProgress is null ? null : new LocalExecutionNodeProvisioningProgressResponse(
                localProgress.JobId,
                localProgress.Platform,
                localProgress.State,
                localProgress.PhaseKey,
                localProgress.PhaseDisplayName,
                localProgress.Message,
                localProgress.PercentComplete,
                localProgress.StartedAt,
                localProgress.UpdatedAt,
                localProgress.RequiresRestart,
                localProgress.ErrorCode,
                localProgress.ErrorMessage,
                localProgress.EstimatedRemainingMinimumSeconds,
                localProgress.EstimatedRemainingMaximumSeconds));
    }

    public async Task<ExecutionCapacityActionResponse> SelectOnboardingModeAsync(
        SelectExecutionOnboardingModeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<ExecutionOnboardingMode>(request.Mode, true, out var mode) ||
            mode == ExecutionOnboardingMode.None)
            return await FailureAsync("invalid_mode", "Choose either this machine or another machine.", cancellationToken);
        if (mode == ExecutionOnboardingMode.Local && !IsSupportedLocalPlatform())
            return await FailureAsync("unsupported_local_platform", "This operating system cannot host a certified execution node.", cancellationToken);

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

    public async Task<ClaimExecutionNodeResponse> ClaimNodeAsync(
        ClaimExecutionNodeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { ValidateClaim(request); }
        catch (ArgumentException)
        {
            return new ClaimExecutionNodeResponse(
                false, "invalid_node_claim", "The execution-node enrollment claim is invalid.", null, null);
        }
        var now = timeProvider.GetUtcNow();
        var tokenHash = Hash(request.EnrollmentToken);
        var enrollment = await dbContext.ExecutionNodeEnrollments
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (enrollment is null || enrollment.Status != ExecutionEnrollmentStatus.Available || enrollment.ExpiresAt <= now)
            return new ClaimExecutionNodeResponse(false, "invalid_enrollment", "The enrollment is invalid, expired, or already used.", null, null);

        var receipt = Base64Url(RandomNumberGenerator.GetBytes(32));
        var node = new ExecutionNode
        {
            Id = Guid.NewGuid(),
            ExecutionPoolId = enrollment.ExecutionPoolId,
            Name = request.Name.Trim(),
            MachineName = request.MachineName.Trim(),
            OperatingSystem = request.OperatingSystem.Trim().ToLowerInvariant(),
            Architecture = request.Architecture.Trim().ToLowerInvariant(),
            NodeVersion = request.NodeVersion.Trim(),
            ProtocolVersion = request.ProtocolVersion.Trim(),
            Status = ExecutionNodeStatus.PendingApproval,
            CertificateThumbprint = NormalizeHex(request.CertificateThumbprint),
            CertificateSerialNumber = NormalizeHex(request.CertificateSerialNumber),
            CertificateExpiresAt = request.CertificateExpiresAt,
            CertificateSigningRequestPem = request.CertificateSigningRequestPem.Trim(),
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
            $"Execution node {node.Name} claimed enrollment {enrollment.Id} and is pending approval.",
            cancellationToken: cancellationToken);
        return new ClaimExecutionNodeResponse(true, null, "Node enrolled and awaiting administrator approval.", node.Id, receipt);
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
            return await FailureAsync("node_not_found", "The execution node was not found.", cancellationToken);
        if (node.Status != ExecutionNodeStatus.PendingApproval)
            return await FailureAsync("node_not_pending", "Only pending execution nodes can be approved.", cancellationToken);
        if (!IdentityAndProvidersQualify(node, now))
            return await FailureAsync("node_not_qualified", "The node does not have a current identity and certified builder/runtime provider.", cancellationToken);

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
        node.CertificateExpiresAt = issued.ExpiresAt;
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
            $"Approved execution node {node.Name} for pool {node.ExecutionPoolId}.",
            cancellationToken: cancellationToken);
        return await SuccessAsync("Execution node approved; waiting for its authenticated control connection.", cancellationToken);
    }

    public async Task<ExecutionCapacityActionResponse> RejectNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        var node = await dbContext.ExecutionNodes
            .SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is null)
            return await FailureAsync("node_not_found", "The execution node was not found.", cancellationToken);
        if (node.Status != ExecutionNodeStatus.PendingApproval)
            return await FailureAsync("node_not_pending", "Only a pending execution node can be rejected.", cancellationToken);
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
            $"Rejected pending execution node {node.Name}.", cancellationToken: cancellationToken);
        return await SuccessAsync("Execution node rejected.", cancellationToken);
    }

    public async Task<bool> RecordHeartbeatAsync(
        Guid nodeId,
        ExecutionNodeHeartbeatRequest request,
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
        node.LastHeartbeatAt = now;
        node.UpdatedAt = now;
        dbContext.ExecutionNodeProviders.RemoveRange(node.Providers);
        node.Providers = request.Providers.Select(provider => Map(node.Id, provider, now)).ToList();
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ExecutionNodeCertificateResponse> GetOperationalCertificateAsync(
        Guid nodeId,
        ExecutionNodeCertificateRequest request,
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

    public async Task<ExecutionNodeCertificateResponse> RotateOperationalCertificateAsync(
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
        out ExecutionNodeCertificateResponse? error)
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
            node.CertificateExpiresAt = issued.ExpiresAt;
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

    private static ExecutionNodeCertificateResponse CertificateResponse(ExecutionNode node, string message) =>
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
        !string.IsNullOrWhiteSpace(node.CertificateThumbprint) &&
        node.CertificateExpiresAt > now &&
        string.Equals(node.ProtocolVersion, CurrentProtocolVersion, StringComparison.Ordinal) &&
        Version.TryParse(node.NodeVersion, out var version) && version >= MinimumNodeVersion &&
        ImagePolicyConfigured() && node.Providers.Any(provider =>
            ProviderQualifies(provider, now, ExecutionWorkloadKind.Builder) &&
            ProviderQualifies(provider, now, ExecutionWorkloadKind.Runtime));

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
                ? Required("node", "Execution node", "No execution node has enrolled.",
                    enrollment is null ? "Create an enrollment and connect a daemon." : "Use the one-time enrollment on an execution-node machine.")
                : Passed("node", "Execution node", $"Detected {nodes.Count} enrolled execution node{(nodes.Count == 1 ? string.Empty : "s")}.")
        };
        var pending = nodes.Count(x => x.Status == ExecutionNodeStatus.PendingApproval);
        result.Add(pending > 0
            ? Required("approval", "Administrator approval", $"{pending} execution node{(pending == 1 ? " is" : "s are")} awaiting approval.", "Review the node identity and approve it.")
            : isReady
                ? Passed("approval", "Administrator approval", "A qualifying execution node is approved.")
                : Required("approval", "Administrator approval", "No qualifying node is approved.", "Enroll and approve an execution node."));
        result.Add(isReady
            ? Passed("capacity", "Certified execution capacity", $"{readyCount} node{(readyCount == 1 ? " is" : "s are")} ready for builds and runtimes.")
            : Required("capacity", "Certified execution capacity", "No approved, connected node currently has certified builder and runtime capacity.",
                "Connect a compatible node with current certification, images, identity, and allocatable resources."));
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

    private async Task<IReadOnlyList<ExecutionCapacityCheckResponse>> LocalPrerequisiteChecksAsync(
        IReadOnlyList<ExecutionNode> nodes,
        LocalExecutionNodeProvisioningProgress? progress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var localNode = nodes.FirstOrDefault(node =>
            string.Equals(node.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.OperatingSystem, LocalOperatingSystem(), StringComparison.OrdinalIgnoreCase));
        var providers = localNode?.Providers ?? [];
        var runtimeInstalled = localNode is not null || progress?.State == "completed";
        var signedImageReady = providers.Any(provider => provider.IsAvailable && IsSha256(provider.GuestImageDigest));
        var certificationReady = providers.Any(provider => provider.IsAvailable &&
            string.Equals(provider.CertificationSuiteVersion, RuntimePolicy.RequiredCertificationSuiteVersion, StringComparison.Ordinal) &&
            IsSha256(provider.CertificationEvidenceDigest) && provider.CertifiedAt <= now &&
            (provider.CertificationExpiresAt is null || provider.CertificationExpiresAt > now));
        if (OperatingSystem.IsWindows())
        {
            var host = windowsHostProbe is null ? null : await windowsHostProbe.ProbeAsync(cancellationToken);
            return
            [
                host?.IsSupportedEdition == true
                    ? Passed("windows-edition", "Supported Windows edition", $"{host.ProductName} ({host.EditionId}) supports Hyper-V.")
                    : Required("windows-edition", "Supported Windows edition", host is null ? "Windows edition has not been inspected." : $"{host.ProductName} ({host.EditionId}) does not support Hyper-V.", "Use Windows Pro, Enterprise, Education, or Server."),
                host?.HardwareRequirementsSatisfied == true
                    ? Passed("windows-virtualization", "Hardware virtualization", "Firmware virtualization, SLAT, DEP, memory, and hypervisor requirements are satisfied.")
                    : Required("windows-virtualization", "Hardware virtualization", "Firmware virtualization, SLAT, DEP, memory, or hypervisor support is unavailable.", "Enable virtualization in firmware and provide at least 4 GB of physical memory."),
                host?.FeatureState == WindowsOptionalFeatureState.Enabled
                    ? Passed("windows-hyperv", "Hyper-V feature", "The Hyper-V feature is enabled.")
                    : Required("windows-hyperv", "Hyper-V feature", $"Hyper-V state: {host?.FeatureState.ToString() ?? "unknown"}.", "Enable Hyper-V from the elevated local installer."),
                host?.IsRestartPending != true
                    ? Passed("windows-restart", "Restart state", "No Hyper-V restart is pending.")
                    : Required("windows-restart", "Restart state", "Windows must restart before Hyper-V can run workloads.", "Restart this machine and reopen setup."),
                runtimeInstalled ? Passed("windows-runtimehost", "RuntimeHost and ExecutionNode", "The local execution services have enrolled.") : Required("windows-runtimehost", "RuntimeHost and ExecutionNode", "The local execution services are not installed and enrolled.", "Run the elevated local installer."),
                signedImageReady ? Passed("windows-image", "Signed guest image", "The node advertises a verified signed guest image.") : Required("windows-image", "Signed guest image", "No verified signed guest image is advertised.", "Install the certified Windows runtime payload."),
                certificationReady ? Passed("windows-certification", "Provider certification", "Current provider certification is advertised.") : Required("windows-certification", "Provider certification", "Current provider certification is unavailable.", "Install a payload certified for this release.")
            ];
        }
        if (OperatingSystem.IsLinux())
            return
            [
                File.Exists("/dev/kvm")
                    ? Passed("linux-kvm", "KVM access", "/dev/kvm is present.")
                    : Required("linux-kvm", "KVM access", "/dev/kvm is unavailable.", "Enable KVM and grant the RuntimeHost service access."),
                File.Exists("/sys/fs/cgroup/cgroup.controllers")
                    ? Passed("linux-cgroups", "cgroups v2", "The cgroup v2 controller filesystem is present.")
                    : Required("linux-cgroups", "cgroups v2", "The cgroup v2 controller filesystem is unavailable.", "Enable a unified cgroup v2 hierarchy."),
                providers.Any(provider => provider.ProviderId == "firecracker-kvm")
                    ? Passed("linux-firecracker", "Firecracker and jailer", "The node reports its Firecracker provider inventory.")
                    : Required("linux-firecracker", "Firecracker and jailer", "Firecracker/jailer inventory has not been reported.", "Install the pinned Firecracker and jailer payload with required capabilities."),
                runtimeInstalled ? Passed("linux-runtimehost", "RuntimeHost and ExecutionNode", "The local execution services have enrolled.") : Required("linux-runtimehost", "RuntimeHost and ExecutionNode", "The local execution services are not installed and enrolled.", "Install and enroll the Linux execution package."),
                signedImageReady ? Passed("linux-image", "Signed guest image", "The node advertises a verified signed guest image.") : Required("linux-image", "Signed guest image", "No verified signed guest image is advertised.", "Install the certified Linux guest image variant."),
                certificationReady ? Passed("linux-certification", "Provider certification", "Current provider certification is advertised.") : Required("linux-certification", "Provider certification", "Current provider certification is unavailable.", "Install a payload certified for this release.")
            ];
        if (OperatingSystem.IsMacOS())
            return
            [
                Environment.OSVersion.Version.Major >= 13
                    ? Passed("macos-version", "Supported macOS version", $"Detected macOS {Environment.OSVersion.Version}.")
                    : Required("macos-version", "Supported macOS version", "This macOS version is unsupported.", "Upgrade to a supported macOS release."),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is
                    System.Runtime.InteropServices.Architecture.Arm64 or System.Runtime.InteropServices.Architecture.X64
                    ? Passed("macos-hardware", "Supported Mac hardware", "The Mac architecture supports the packaged virtualization helper.")
                    : Required("macos-hardware", "Supported Mac hardware", "This Mac architecture is unsupported.", "Use an Apple silicon or supported Intel Mac."),
                providers.Any(provider => provider.ProviderId == "apple-virtualization")
                    ? Passed("macos-entitlement", "Virtualization.framework entitlement", "The signed helper reports Apple Virtualization inventory.")
                    : Required("macos-entitlement", "Virtualization.framework entitlement", "The entitled Apple Virtualization helper has not reported inventory.", "Install the signed and notarized macOS package."),
                runtimeInstalled ? Passed("macos-runtimehost", "RuntimeHost and ExecutionNode", "The local execution services have enrolled.") : Required("macos-runtimehost", "RuntimeHost and ExecutionNode", "The local execution services are not installed and enrolled.", "Install and enroll the macOS execution package."),
                signedImageReady ? Passed("macos-image", "Signed guest image", "The node advertises a verified signed guest image.") : Required("macos-image", "Signed guest image", "No verified signed guest image is advertised.", "Install the certified macOS guest image variant."),
                certificationReady ? Passed("macos-certification", "Provider certification", "Current provider certification is advertised.") : Required("macos-certification", "Provider certification", "Current provider certification is unavailable.", "Install a payload certified for this release.")
            ];
        return [Required("platform", "Supported platform", "This platform is unsupported.", "Use a remote Windows, Linux, or macOS node.")];
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

    private static ExecutionEnrollmentResponse Map(ExecutionNodeEnrollment enrollment) => new(
        enrollment.Id, enrollment.ExecutionPoolId, enrollment.Status.ToString().ToLowerInvariant(),
        enrollment.ExpiresAt);

    private static ExecutionNodeProvider Map(
        Guid nodeId,
        RegisterExecutionNodeProviderRequest provider,
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
        CertifiedAt = provider.CertifiedAt,
        CertificationExpiresAt = provider.CertificationExpiresAt,
        SupportsBuilderWorkloads = provider.SupportsBuilderWorkloads,
        SupportsRuntimeWorkloads = provider.SupportsRuntimeWorkloads,
        IsAvailable = provider.IsAvailable,
        UnavailableReason = string.IsNullOrWhiteSpace(provider.UnavailableReason)
            ? null : provider.UnavailableReason.Trim()[..Math.Min(1024, provider.UnavailableReason.Trim().Length)],
        UpdatedAt = now
    };

    private async Task<ExecutionCapacityActionResponse> SuccessAsync(string message, CancellationToken cancellationToken) =>
        new(true, null, message, await GetOnboardingStatusAsync(cancellationToken));

    private async Task<ExecutionCapacityActionResponse> FailureAsync(string code, string message, CancellationToken cancellationToken) =>
        new(false, code, message, await GetOnboardingStatusAsync(cancellationToken));

    private static void ValidateClaim(ClaimExecutionNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || request.EnrollmentToken.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 160 ||
            string.IsNullOrWhiteSpace(request.MachineName) || request.MachineName.Length > 255 ||
            string.IsNullOrWhiteSpace(request.OperatingSystem) || request.OperatingSystem.Length > 32 ||
            string.IsNullOrWhiteSpace(request.Architecture) || request.Architecture.Length > 32 ||
            !Version.TryParse(request.NodeVersion, out _) || request.ProtocolVersion != CurrentProtocolVersion ||
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
