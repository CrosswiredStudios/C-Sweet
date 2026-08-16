using CSweet.Office.Contracts.ControlPlane;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    IOptions<AgentRuntimeManagerOptions>? runtimeOptions = null,
    ILocalOfficeCapacityProbe? localCapacityProbe = null) : IExecutionFleetService
{
    private const string CurrentProtocolVersion = "1.0";
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AssistedSetupLifetime = TimeSpan.FromMinutes(5);
    internal static readonly Version MinimumNodeVersion = new(0, 1, 0);
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
        await ExpireLocalSetupSessionsAsync(now, cancellationToken);
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
        var localCapacity = DetectLocalCapacity();
        IReadOnlyList<ExecutionCapacityCheckResponse> localPrerequisites = localCapacity.IsSupported
            ? [Passed("local-capacity", "Local Office capacity", "This Windows host has safe capacity for C-Sweet Office.")]
            : [Required("local-capacity", "Local Office capacity", localCapacity.UnavailableReason ??
                "Assisted local setup is unavailable.", "Choose another machine or free resources and try again.")];
        return new ExecutionCapacityOnboardingResponse(
            mode,
            isReady,
            ready.Length,
            pending,
            localCapacity.IsSupported,
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
            null,
            localCapacity,
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

    public async Task<LocalOfficeSetupActionResponse> CreateLocalSetupSessionAsync(
        CreateLocalOfficeSetupSessionRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        await ExpireLocalSetupSessionsAsync(now, cancellationToken);
        var existing = await dbContext.LocalOfficeSetupSessions
            .Include(x => x.ExecutionNode)
            .Where(x => x.CreatedByUserId == createdByUserId &&
                (x.Status == LocalOfficeSetupSessionStatus.Created ||
                 x.Status == LocalOfficeSetupSessionStatus.Redeemed ||
                 x.Status == LocalOfficeSetupSessionStatus.Connected ||
                 x.Status == LocalOfficeSetupSessionStatus.Ready))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var existingResponse = Map(existing, fleetOptions?.Value.WindowsPackageOverrideUrl, null,
                DevelopmentLauncherConfigured ? "server" : "protocol");
            if (existingResponse.State != "failed")
                return new LocalOfficeSetupActionResponse(true, "local_setup_in_progress",
                    existingResponse.State == "ready"
                        ? "The local Office is already ready."
                        : "Local Office setup is already in progress.",
                    existingResponse, await GetOnboardingStatusAsync(cancellationToken));

            existing.Status = LocalOfficeSetupSessionStatus.Failed;
            existing.ErrorCode ??= existingResponse.ErrorCode ?? "setup_failed";
            existing.ErrorMessage ??= existingResponse.ErrorMessage ?? existingResponse.Message;
            existing.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var capacity = DetectLocalCapacity();
        if (!LocalOfficeCapacityCalculator.Contains(capacity, request.AllocatableCpuCount,
                request.AllocatableMemoryMb, request.AllocatableDiskMb))
            return await LocalFailureAsync("invalid_capacity",
                "The selected allocation is outside this machine's current safe limits.", cancellationToken);

        var presetKey = request.PresetKey.Trim().ToLowerInvariant();
        if (presetKey is not ("small" or "balanced" or "performance" or "custom"))
            return await LocalFailureAsync("invalid_preset", "Choose a valid Office capacity profile.", cancellationToken);
        if (presetKey != "custom")
        {
            var preset = capacity.Presets.Single(x => x.Key == presetKey);
            if (preset.CpuCount != request.AllocatableCpuCount || preset.MemoryMb != request.AllocatableMemoryMb ||
                preset.DiskMb != request.AllocatableDiskMb)
                return await LocalFailureAsync("stale_capacity",
                    "This machine's capacity changed. Review the refreshed recommendation and try again.", cancellationToken);
        }

        var controlPlaneOrigin = PublicControlPlaneUrl(appConfiguration);
        var windowsPackageUrl = fleetOptions?.Value.WindowsPackageOverrideUrl;
        if (controlPlaneOrigin is null)
            return await LocalFailureAsync("control_plane_url_unavailable",
                "Assisted setup requires a public HTTPS Office control-plane URL.", cancellationToken);
        var controlPlaneCertificateSha256 = NormalizeOptionalSha256(
            appConfiguration?["CSweet:ExecutionGateway:PublicCertificateSha256"]);
        if (controlPlaneCertificateSha256 is null && DevelopmentLauncherConfigured)
        {
            var controlPlaneUri = new Uri(controlPlaneOrigin, UriKind.Absolute);
            if (!controlPlaneUri.IsLoopback)
                return await LocalFailureAsync("control_plane_certificate_unavailable",
                    "Assisted setup requires an explicitly configured certificate fingerprint for a non-loopback control plane.",
                    cancellationToken);
            try
            {
                controlPlaneCertificateSha256 = await ProbeServerCertificateSha256Async(
                    controlPlaneUri, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return await LocalFailureAsync("control_plane_certificate_unavailable",
                    "C-Sweet could not inspect the local control-plane certificate. Try again after the execution gateway is ready.",
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or AuthenticationException)
            {
                return await LocalFailureAsync("control_plane_certificate_unavailable",
                    "C-Sweet could not inspect the local control-plane certificate. Try again after the execution gateway is ready.",
                    cancellationToken);
            }
        }
        var packageUri = Uri.TryCreate(windowsPackageUrl, UriKind.Absolute, out var configuredPackageUri) &&
            configuredPackageUri.Scheme == Uri.UriSchemeHttps
                ? configuredPackageUri
                : null;
        var handoff = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new LocalOfficeSetupSession
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = createdByUserId,
            HandoffSecretHash = Hash(handoff),
            MachineBindingHash = MachineBindingHash(Environment.MachineName, "windows",
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
            OperatingSystem = "windows",
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ControlPlaneOrigin = controlPlaneOrigin,
            ControlPlaneCertificateSha256 = controlPlaneCertificateSha256,
            PresetKey = presetKey,
            AllocatableCpuCount = request.AllocatableCpuCount,
            AllocatableMemoryMb = request.AllocatableMemoryMb,
            AllocatableDiskMb = request.AllocatableDiskMb,
            MaximumConcurrentWorkloads = LocalOfficeCapacityCalculator.MaximumConcurrentWorkloads(
                request.AllocatableCpuCount),
            ExpiresAt = now.Add(AssistedSetupLifetime),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.LocalOfficeSetupSessions.Add(session);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(session).State = EntityState.Detached;
            var concurrent = await dbContext.LocalOfficeSetupSessions.AsNoTracking()
                .Where(x => x.CreatedByUserId == createdByUserId &&
                    (x.Status == LocalOfficeSetupSessionStatus.Created ||
                     x.Status == LocalOfficeSetupSessionStatus.Redeemed ||
                     x.Status == LocalOfficeSetupSessionStatus.Connected))
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (concurrent is null) throw;
            return new LocalOfficeSetupActionResponse(true, "local_setup_in_progress",
                "Local Office setup is already in progress.",
                Map(concurrent, fleetOptions?.Value.WindowsPackageOverrideUrl, null,
                    DevelopmentLauncherConfigured ? "server" : "protocol"),
                await GetOnboardingStatusAsync(cancellationToken));
        }
        await auditWriter.WriteAsync("office.local-setup.created", nameof(LocalOfficeSetupSession), session.Id,
            $"Created an assisted Windows Office setup session using the {presetKey} profile.",
            cancellationToken: cancellationToken);

        var launchUri = LocalSetupLaunchUri(session, handoff);
        var launchMethod = DevelopmentLauncherConfigured ? "server" : "protocol";
        return new LocalOfficeSetupActionResponse(true, null, "Local Office setup is ready.",
            Map(session, packageUri?.AbsoluteUri, launchUri, launchMethod),
            await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task<LocalOfficeSetupActionResponse> LaunchLocalSetupSessionAsync(
        Guid sessionId,
        Guid createdByUserId,
        LaunchLocalOfficeSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        await ExpireLocalSetupSessionsAsync(now, cancellationToken);
        var session = await dbContext.LocalOfficeSetupSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.CreatedByUserId == createdByUserId, cancellationToken);
        if (session is null)
            return await LocalFailureAsync("session_not_found", "The local setup session was not found.", cancellationToken);
        if (session.Status != LocalOfficeSetupSessionStatus.Created || session.ExpiresAt <= now)
            return await LocalFailureAsync("session_not_launchable",
                "This setup session can no longer request administrator approval.", cancellationToken);
        if (!TryReadLaunchHandoff(request.LaunchUri, session, out var handoff))
            return await LocalFailureAsync("invalid_handoff", "The secure setup handoff is invalid.", cancellationToken);

        var certificateParameter = string.IsNullOrWhiteSpace(session.ControlPlaneCertificateSha256)
            ? string.Empty
            : $"&certificate={session.ControlPlaneCertificateSha256}";
        var launchUri = $"csweet-office://enroll/v1?session={session.Id:D}&origin={Uri.EscapeDataString(session.ControlPlaneOrigin)}{certificateParameter}#handoff={handoff}";
        var launchMethod = DevelopmentLauncherConfigured ? "server" : "protocol";
        if (launchMethod == "server")
        {
            var launch = TryStartWindowsDevelopmentSetup(session.Id, launchUri);
            if (!launch.Started)
                return new LocalOfficeSetupActionResponse(false, launch.ErrorCode,
                    launch.ErrorMessage ?? "Windows setup could not be started.",
                    Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, launchUri, launchMethod),
                    await GetOnboardingStatusAsync(cancellationToken));
        }
        session.AdministratorApprovalRequestedAt = now;
        session.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync("office.local-setup.launched", nameof(LocalOfficeSetupSession), session.Id,
            "Requested administrator approval for assisted Windows Office setup.",
            cancellationToken: cancellationToken);
        return new LocalOfficeSetupActionResponse(true, null,
            "Windows administrator approval requested.",
            Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, launchUri, launchMethod),
            await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task<LocalOfficeSetupActionResponse> GetLocalSetupSessionAsync(
        Guid sessionId,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireLocalSetupSessionsAsync(now, cancellationToken);
        await ReconcileClaimedLocalSetupSessionsAsync(createdByUserId, cancellationToken);
        var session = await dbContext.LocalOfficeSetupSessions.AsNoTracking()
            .Include(x => x.ExecutionNode)
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.CreatedByUserId == createdByUserId, cancellationToken);
        if (session is null)
            return await LocalFailureAsync("session_not_found", "The local setup session was not found.", cancellationToken);
        return new LocalOfficeSetupActionResponse(true, null, "Local Office setup status loaded.",
            Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, null,
                DevelopmentLauncherConfigured ? "server" : "protocol"),
            await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task<LocalOfficeSetupActionResponse> GetActiveLocalSetupSessionAsync(
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireLocalSetupSessionsAsync(now, cancellationToken);
        await ReconcileClaimedLocalSetupSessionsAsync(createdByUserId, cancellationToken);
        var session = await dbContext.LocalOfficeSetupSessions
            .Include(x => x.ExecutionNode)
            .Where(x => x.CreatedByUserId == createdByUserId &&
                (x.Status == LocalOfficeSetupSessionStatus.Created ||
                 x.Status == LocalOfficeSetupSessionStatus.Redeemed ||
                 x.Status == LocalOfficeSetupSessionStatus.Connected ||
                 x.Status == LocalOfficeSetupSessionStatus.Ready))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var mapped = session is null ? null : Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, null,
            DevelopmentLauncherConfigured ? "server" : "protocol");
        if (session is not null && mapped?.State == "failed")
        {
            session.Status = LocalOfficeSetupSessionStatus.Failed;
            session.ErrorCode ??= mapped.ErrorCode ?? "setup_failed";
            session.ErrorMessage ??= mapped.ErrorMessage ?? mapped.Message;
            session.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            mapped = null;
        }
        return new LocalOfficeSetupActionResponse(true, null, "Local Office setup status loaded.",
            mapped,
            await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task<LocalOfficeSetupActionResponse> RefreshLocalSetupSessionHandoffAsync(
        Guid sessionId,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await ExpireLocalSetupSessionsAsync(now, cancellationToken);
        var session = await dbContext.LocalOfficeSetupSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.CreatedByUserId == createdByUserId, cancellationToken);
        if (session is null)
            return await LocalFailureAsync("session_not_found", "The local setup session was not found.", cancellationToken);
        if (session.Status != LocalOfficeSetupSessionStatus.Created)
            return await LocalFailureAsync("session_not_refreshable",
                "Administrator approval can only be requested while local setup is waiting to begin.", cancellationToken);

        var handoff = Base64Url(RandomNumberGenerator.GetBytes(32));
        session.HandoffSecretHash = Hash(handoff);
        session.AdministratorApprovalRequestedAt = null;
        session.ExpiresAt = now.Add(AssistedSetupLifetime);
        session.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync("office.local-setup.handoff-refreshed",
            nameof(LocalOfficeSetupSession), session.Id,
            "Refreshed the one-use handoff for an existing local Office setup session.",
            cancellationToken: cancellationToken);
        return new LocalOfficeSetupActionResponse(true, null, "Local Office setup is ready to continue.",
            Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl,
                LocalSetupLaunchUri(session, handoff),
                DevelopmentLauncherConfigured ? "server" : "protocol"),
            await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task<RedeemAssistedOfficeSetupResponse> RedeemLocalSetupSessionAsync(
        RedeemAssistedOfficeSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.HandoffSecret) || request.HandoffSecret.Length > 256 ||
            string.IsNullOrWhiteSpace(request.MachineName) || request.MachineName.Length > 255 ||
            !string.Equals(request.OperatingSystem, "windows", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.Architecture) || request.Architecture.Length > 32 ||
            !Version.TryParse(request.OfficeVersion, out _))
            return RedeemFailure("invalid_request", "The assisted Office setup request is invalid.");

        var now = timeProvider.GetUtcNow();
        var secretHash = Hash(request.HandoffSecret);
        var session = await dbContext.LocalOfficeSetupSessions
            .SingleOrDefaultAsync(x => x.HandoffSecretHash == secretHash, cancellationToken);
        if (session is null || session.Status != LocalOfficeSetupSessionStatus.Created || session.ExpiresAt <= now)
            return RedeemFailure("invalid_handoff", "The setup handoff is invalid, expired, or already used.");
        var machineBinding = MachineBindingHash(request.MachineName, request.OperatingSystem, request.Architecture);
        if (string.IsNullOrWhiteSpace(session.MachineBindingHash) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(session.MachineBindingHash), Encoding.ASCII.GetBytes(machineBinding)))
            return RedeemFailure("machine_mismatch", "The setup handoff was created for a different Windows machine.");

        await EnsureDefaultPoolAsync(cancellationToken);
        var pool = await DefaultPoolAsync(cancellationToken);
        var enrollmentToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var enrollment = new ExecutionNodeEnrollment
        {
            Id = Guid.NewGuid(),
            ExecutionPoolId = pool.Id,
            TokenHash = Hash(enrollmentToken),
            ReceiptHash = Hash(Base64Url(RandomNumberGenerator.GetBytes(32))),
            Status = ExecutionEnrollmentStatus.Available,
            ExpiresAt = now.Add(EnrollmentLifetime),
            CreatedAt = now
        };
        dbContext.ExecutionNodeEnrollments.Add(enrollment);
        session.ExecutionNodeEnrollmentId = enrollment.Id;
        session.Status = LocalOfficeSetupSessionStatus.Redeemed;
        session.RedeemedAt = now;
        session.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync("office.local-setup.redeemed", nameof(LocalOfficeSetupSession), session.Id,
            "Redeemed a one-use assisted Office setup handoff.", cancellationToken: cancellationToken);
        return new RedeemAssistedOfficeSetupResponse(true, null, "Assisted Office setup redeemed.",
            session.Id, enrollmentToken, session.ControlPlaneOrigin, session.ControlPlaneCertificateSha256,
            session.AllocatableCpuCount, session.AllocatableMemoryMb, session.AllocatableDiskMb,
            session.MaximumConcurrentWorkloads, true);
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

        var assistedSession = await dbContext.LocalOfficeSetupSessions.SingleOrDefaultAsync(x =>
            x.ExecutionNodeEnrollmentId == enrollment.Id &&
            x.Status == LocalOfficeSetupSessionStatus.Redeemed, cancellationToken);
        if (request.AssistedSetupSessionId is { } assistedSessionId &&
            assistedSession?.Id != assistedSessionId)
            return new ClaimOfficeResponse(false, "assisted_session_mismatch",
                "The assisted setup session does not match this Office.", null, null);
        if (assistedSession is not null)
        {
            var binding = MachineBindingHash(request.MachineName, request.OperatingSystem,
                request.Architecture);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(assistedSession.MachineBindingHash ?? string.Empty),
                    Encoding.ASCII.GetBytes(binding)))
                return new ClaimOfficeResponse(false, "assisted_session_mismatch",
                    "The assisted setup session does not match this Office.", null, null);
        }

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
        if (assistedSession is not null)
        {
            assistedSession.ExecutionNodeId = node.Id;
            assistedSession.Status = LocalOfficeSetupSessionStatus.Connected;
            assistedSession.ConnectedAt = now;
            assistedSession.UpdatedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            "execution-node.enrollment.claimed",
            nameof(ExecutionNode), node.Id,
            $"Office {node.Name} claimed enrollment {enrollment.Id} and is pending approval.",
            cancellationToken: cancellationToken);
        if (assistedSession is not null)
        {
            var approval = await ApproveNodeAsync(node.Id, cancellationToken);
            if (!approval.Succeeded)
            {
                assistedSession.Status = LocalOfficeSetupSessionStatus.Failed;
                assistedSession.ErrorCode = approval.ErrorCode;
                assistedSession.ErrorMessage = approval.Message;
                assistedSession.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
                return new ClaimOfficeResponse(false, approval.ErrorCode,
                    "The assisted Office connected but could not be approved automatically.", node.Id, receipt);
            }
            assistedSession.Status = LocalOfficeSetupSessionStatus.Ready;
            assistedSession.CompletedAt = timeProvider.GetUtcNow();
            assistedSession.UpdatedAt = assistedSession.CompletedAt.Value;
            await dbContext.SaveChangesAsync(cancellationToken);
            return new ClaimOfficeResponse(true, null, "Office enrolled and approved automatically.", node.Id, receipt);
        }
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

    internal static async Task<string> ProbeServerCertificateSha256Async(
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The certificate endpoint must use HTTPS.", nameof(endpoint));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.DnsSafeHost, endpoint.IsDefaultPort ? 443 : endpoint.Port,
            timeout.Token);
        X509Certificate2? remoteCertificate = null;
        using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is not null)
                remoteCertificate = new X509Certificate2(certificate);
            return true;
        });
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.DnsSafeHost,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);
            if (remoteCertificate is null)
                throw new AuthenticationException("The control plane did not present a TLS certificate.");
            return Convert.ToHexString(SHA256.HashData(remoteCertificate.RawData)).ToLowerInvariant();
        }
        finally
        {
            remoteCertificate?.Dispose();
        }
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

    private async Task<LocalOfficeSetupActionResponse> LocalFailureAsync(
        string code,
        string message,
        CancellationToken cancellationToken) =>
        new(false, code, message, null, await GetOnboardingStatusAsync(cancellationToken));

    private static RedeemAssistedOfficeSetupResponse RedeemFailure(string code, string message) =>
        new(false, code, message, null, null, null, null, 0, 0, 0, 0, true);

    private LocalOfficeCapacityResponse DetectLocalCapacity()
    {
        if (localCapacityProbe is not null) return localCapacityProbe.GetCapacity();
        var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalMemory <= 0) totalMemory = 8L * 1024 * 1024 * 1024;
        long freeDisk;
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            freeDisk = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (IOException)
        {
            freeDisk = 0;
        }
        return LocalOfficeCapacityCalculator.Calculate(
            Environment.ProcessorCount, totalMemory, freeDisk, OperatingSystem.IsWindows());
    }

    private async Task ExpireLocalSetupSessionsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var expired = await dbContext.LocalOfficeSetupSessions.Where(x => x.ExpiresAt <= now &&
            x.Status == LocalOfficeSetupSessionStatus.Created).ToListAsync(cancellationToken);
        if (expired.Count == 0) return;
        foreach (var session in expired)
        {
            session.Status = LocalOfficeSetupSessionStatus.Expired;
            session.UpdatedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileClaimedLocalSetupSessionsAsync(
        Guid createdByUserId,
        CancellationToken cancellationToken)
    {
        var sessions = await dbContext.LocalOfficeSetupSessions
            .Where(x => x.CreatedByUserId == createdByUserId &&
                x.Status == LocalOfficeSetupSessionStatus.Redeemed &&
                x.ExecutionNodeEnrollmentId != null && x.ExecutionNodeId == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            var enrollment = await dbContext.ExecutionNodeEnrollments.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == session.ExecutionNodeEnrollmentId, cancellationToken);
            if (enrollment?.ExecutionNodeId is not { } nodeId) continue;
            var node = await dbContext.ExecutionNodes.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
            if (node is null) continue;

            var binding = MachineBindingHash(node.MachineName, node.OperatingSystem, node.Architecture);
            if (string.IsNullOrWhiteSpace(session.MachineBindingHash) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(session.MachineBindingHash), Encoding.ASCII.GetBytes(binding)))
            {
                session.Status = LocalOfficeSetupSessionStatus.Failed;
                session.ErrorCode = "assisted_session_mismatch";
                session.ErrorMessage = "The connected Office does not match this assisted setup session.";
                session.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            session.ExecutionNodeId = node.Id;
            session.Status = LocalOfficeSetupSessionStatus.Connected;
            session.ConnectedAt = enrollment.ClaimedAt ?? timeProvider.GetUtcNow();
            session.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);

            ExecutionCapacityActionResponse approval;
            if (node.Status == ExecutionNodeStatus.PendingApproval)
                approval = await ApproveNodeAsync(node.Id, cancellationToken);
            else if (node.Status == ExecutionNodeStatus.Ready)
                approval = new ExecutionCapacityActionResponse(true, null, "Office is already approved.",
                    await GetOnboardingStatusAsync(cancellationToken));
            else
                approval = new ExecutionCapacityActionResponse(false, "node_not_pending",
                    "The connected Office cannot be approved from this setup session.",
                    await GetOnboardingStatusAsync(cancellationToken));

            if (!approval.Succeeded)
            {
                session.Status = LocalOfficeSetupSessionStatus.Failed;
                session.ErrorCode = approval.ErrorCode;
                session.ErrorMessage = approval.Message;
                session.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            session.Status = LocalOfficeSetupSessionStatus.Ready;
            session.CompletedAt = timeProvider.GetUtcNow();
            session.UpdatedAt = session.CompletedAt.Value;
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.WriteAsync("office.local-setup.claim-reconciled",
                nameof(LocalOfficeSetupSession), session.Id,
                "Recovered an assisted Office setup after its exact enrollment was claimed.",
                cancellationToken: cancellationToken);
        }
    }

    private LocalOfficeSetupSessionResponse Map(
        LocalOfficeSetupSession session,
        string? windowsPackageUrl,
        string? launchUri,
        string? launchMethod = null)
    {
        var status = session.Status == LocalOfficeSetupSessionStatus.Connected &&
            session.ExecutionNode is { Status: ExecutionNodeStatus.Ready, LastHeartbeatAt: not null }
            ? LocalOfficeSetupSessionStatus.Ready
            : session.Status;
        (string phaseKey, string phaseName, string message, int percent,
            int? minimumSeconds, int? maximumSeconds) = status switch
        {
            LocalOfficeSetupSessionStatus.Created => ("install", "Administrator approval required",
                "Windows will ask for administrator approval so C-Sweet can create the secure VM runtime.", 0, (int?)null, (int?)null),
            LocalOfficeSetupSessionStatus.Redeemed => ("install", "Preparing your Office",
                "C-Sweet is installing the Office services and preparing the secure runtime images.", 25, (int?)120, (int?)480),
            LocalOfficeSetupSessionStatus.Connected => ("verify", "Running health checks",
                "The Office is connected. C-Sweet is verifying its capacity and secure runtime.", 90, (int?)10, (int?)60),
            LocalOfficeSetupSessionStatus.Ready => ("ready", "Your Office is ready",
                "This Office is connected and healthy.", 100, (int?)0, (int?)0),
            LocalOfficeSetupSessionStatus.Expired => ("install", "Setup session expired",
                "Start again to create a fresh secure setup handoff.", 0, (int?)null, (int?)null),
            LocalOfficeSetupSessionStatus.Revoked => ("install", "Setup session replaced",
                "A newer local setup session replaced this one.", 0, (int?)null, (int?)null),
            LocalOfficeSetupSessionStatus.Failed => ("install", "Setup needs attention",
                session.ErrorMessage ?? "The Office setup did not complete.", 0, (int?)null, (int?)null),
            _ => ("install", "Preparing your Office", "C-Sweet is continuing setup.", 0, (int?)null, (int?)null)
        };
        var windowsProgress = ReadWindowsSetupProgress(session.Id);
        if (windowsProgress is { } progress && status != LocalOfficeSetupSessionStatus.Ready)
        {
            phaseKey = progress.PhaseKey;
            phaseName = progress.PhaseDisplayName;
            message = progress.Message;
            percent = progress.PercentComplete;
            minimumSeconds = progress.EstimatedRemainingMinimumSeconds;
            maximumSeconds = progress.EstimatedRemainingMaximumSeconds;
            if (progress.State == "failed")
            {
                status = LocalOfficeSetupSessionStatus.Failed;
                phaseName = "Setup needs attention";
                message = progress.ErrorMessage ?? progress.Message;
                percent = 0;
            }
            else if (progress.State == "restart-required")
            {
                phaseName = "Windows restart required";
                message = "Restart this computer, then reopen C-Sweet. Office setup will resume automatically.";
            }
            else if (progress.State == "completed" && status == LocalOfficeSetupSessionStatus.Redeemed)
            {
                if (timeProvider.GetUtcNow() - progress.ObservedAt >= TimeSpan.FromSeconds(90))
                {
                    status = LocalOfficeSetupSessionStatus.Failed;
                    phaseKey = "connect";
                    phaseName = "Secure connection did not complete";
                    message = "The Office finished installing but did not complete its secure connection. Start again to retry setup.";
                    percent = 0;
                    minimumSeconds = null;
                    maximumSeconds = null;
                }
                else
                {
                    phaseKey = "connect";
                    phaseName = "Connecting securely";
                    message = "The Office services and runtime images are ready. C-Sweet is waiting for the secure connection.";
                    percent = Math.Max(85, percent);
                    minimumSeconds = 5;
                    maximumSeconds = 60;
                }
            }
        }
        else if (status == LocalOfficeSetupSessionStatus.Redeemed &&
                 session.RedeemedAt is { } redeemedAt &&
                 timeProvider.GetUtcNow() - redeemedAt >= TimeSpan.FromSeconds(45))
        {
            status = LocalOfficeSetupSessionStatus.Failed;
            phaseKey = "install";
            phaseName = "Secure runtime preparation stopped";
            message = "Windows setup did not report progress after administrator approval. Start again to create a fresh setup session.";
            percent = 0;
            minimumSeconds = null;
            maximumSeconds = null;
        }
        return new LocalOfficeSetupSessionResponse(session.Id,
            status.ToString().ToLowerInvariant(), phaseKey, phaseName, message, percent,
            session.ExpiresAt, session.AllocatableCpuCount, session.AllocatableMemoryMb,
            session.AllocatableDiskMb, session.MaximumConcurrentWorkloads,
            windowsPackageUrl, launchUri, session.ErrorCode, session.ErrorMessage,
            minimumSeconds, maximumSeconds, launchMethod,
            session.AdministratorApprovalRequestedAt);
    }

    private static string LocalSetupLaunchUri(LocalOfficeSetupSession session, string handoff)
    {
        var certificateParameter = string.IsNullOrWhiteSpace(session.ControlPlaneCertificateSha256)
            ? string.Empty
            : $"&certificate={session.ControlPlaneCertificateSha256}";
        return $"csweet-office://enroll/v1?session={session.Id:D}&origin={Uri.EscapeDataString(session.ControlPlaneOrigin)}{certificateParameter}#handoff={handoff}";
    }

    private bool DevelopmentLauncherConfigured => OperatingSystem.IsWindows() &&
        fleetOptions?.Value.WindowsDevelopmentLauncherScript is { Length: > 0 } launcher &&
        fleetOptions.Value.WindowsDevelopmentOfficeBootstrapScript is { Length: > 0 } bootstrap &&
        Path.IsPathFullyQualified(launcher) && Path.IsPathFullyQualified(bootstrap) &&
        File.Exists(launcher) && File.Exists(bootstrap);

    private static bool TryReadLaunchHandoff(
        string value,
        LocalOfficeSetupSession session,
        out string handoff)
    {
        handoff = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "csweet-office", StringComparison.Ordinal) ||
            !string.Equals(uri.Host, "enroll", StringComparison.Ordinal) ||
            !string.Equals(uri.AbsolutePath, "/v1", StringComparison.Ordinal) ||
            !Guid.TryParse(QueryValue(uri, "session"), out var requestedSessionId) ||
            requestedSessionId != session.Id ||
            !string.Equals(QueryValue(uri, "origin"), session.ControlPlaneOrigin, StringComparison.Ordinal) ||
            !uri.Fragment.StartsWith("#handoff=", StringComparison.Ordinal))
            return false;
        handoff = Uri.UnescapeDataString(uri.Fragment["#handoff=".Length..]);
        if (string.IsNullOrWhiteSpace(handoff) || handoff.Length > 256) return false;
        var expected = Encoding.ASCII.GetBytes(session.HandoffSecretHash);
        var actual = Encoding.ASCII.GetBytes(Hash(handoff));
        return expected.Length == actual.Length &&
            CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.Ordinal))
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
        }
        return null;
    }

    private DevelopmentLaunchResult TryStartWindowsDevelopmentSetup(Guid sessionId, string launchUri)
    {
        var launcher = fleetOptions!.Value.WindowsDevelopmentLauncherScript!;
        var bootstrap = fleetOptions.Value.WindowsDevelopmentOfficeBootstrapScript!;
        string? handoffPath = null;
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                return new(false, "local_setup_storage_unavailable",
                    "C-Sweet could not create the protected Windows setup handoff.");
            var setupRoot = Path.Combine(localData, "CSweet", "Setup");
            Directory.CreateDirectory(setupRoot);
            handoffPath = Path.Combine(setupRoot, $"office-handoff-{sessionId:N}.secret");
            File.WriteAllText(handoffPath, launchUri, new UTF8Encoding(false));
            File.SetAttributes(handoffPath, FileAttributes.Hidden | FileAttributes.Temporary);

            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", launcher, "-HandoffInputPath", handoffPath,
                "-OfficeBootstrapScript", bootstrap
            }) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Windows did not start the elevated Office setup process.");
            return new(true, null, null);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            DeleteTransientHandoff(handoffPath);
            return new(false, "administrator_approval_cancelled",
                "Administrator approval was cancelled. Try again when you are ready.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
                                           UnauthorizedAccessException or InvalidOperationException)
        {
            DeleteTransientHandoff(handoffPath);
            return new(false, "windows_setup_launch_failed",
                $"C-Sweet could not start Windows setup: {exception.Message}");
        }
    }

    private static void DeleteTransientHandoff(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record DevelopmentLaunchResult(bool Started, string? ErrorCode, string? ErrorMessage);

    private static WindowsSetupProgressDocument? ReadWindowsSetupProgress(Guid sessionId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonData)) return null;
            var setupRoot = Path.GetFullPath(Path.Combine(commonData, "CSweet", "Setup"));
            var path = Path.GetFullPath(Path.Combine(setupRoot,
                $"windows-isolation-{sessionId:N}.json"));
            if (!path.StartsWith(setupRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 64 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var progress = JsonSerializer.Deserialize<WindowsSetupProgressDocument>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (progress is null || progress.SchemaVersion != 1 || progress.JobId != sessionId ||
                progress.State is not ("running" or "restart-required" or "completed" or "failed") ||
                string.IsNullOrWhiteSpace(progress.PhaseKey) || progress.PhaseKey.Length > 64 ||
                string.IsNullOrWhiteSpace(progress.PhaseDisplayName) || progress.PhaseDisplayName.Length > 160 ||
                string.IsNullOrWhiteSpace(progress.Message) || progress.Message.Length > 512 ||
                progress.PercentComplete is < 0 or > 100)
                return null;
            progress.EstimatedRemainingMinimumSeconds = ClampEta(progress.EstimatedRemainingMinimumSeconds);
            progress.EstimatedRemainingMaximumSeconds = ClampEta(progress.EstimatedRemainingMaximumSeconds);
            progress.ObservedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            return progress;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static int? ClampEta(int? value) => value is null ? null : Math.Clamp(value.Value, 0, 86_400);

    private sealed class WindowsSetupProgressDocument
    {
        public int SchemaVersion { get; set; }
        public Guid JobId { get; set; }
        public string State { get; set; } = string.Empty;
        public string PhaseKey { get; set; } = string.Empty;
        public string PhaseDisplayName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public int PercentComplete { get; set; }
        public int? EstimatedRemainingMinimumSeconds { get; set; }
        public int? EstimatedRemainingMaximumSeconds { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
    }

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

    private static string MachineBindingHash(
        string machineName,
        string operatingSystem,
        string architecture) => Hash(string.Join('\n',
            machineName.Trim().ToUpperInvariant(),
            operatingSystem.Trim().ToLowerInvariant(),
            architecture.Trim().ToLowerInvariant()));

    private static string? NormalizeOptionalSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Length == 64 ? normalized : null;
    }

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
