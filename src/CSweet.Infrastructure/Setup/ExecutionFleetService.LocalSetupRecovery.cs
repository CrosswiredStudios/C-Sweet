using System.Security.Cryptography;
using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

/// <summary>
/// Exits for a local Office setup that cannot finish on its own: continuing after the Windows restart
/// that Hyper-V needs, abandoning ("Start over") a stuck session, and expiring a redeemed session whose
/// elevated setup process died. State changes are conditional updates, so a duplicate click or a second
/// API instance cannot launch or cancel the same session twice.
/// </summary>
public sealed partial class ExecutionFleetService
{
    internal const string LocalSetupCancelledCode = "setup_cancelled";
    internal const string LocalSetupInterruptedCode = "office_setup_interrupted";

    /// <summary>A restart request stays resumable this long; afterwards the session expires.</summary>
    internal static readonly TimeSpan RestartResumeWindow = TimeSpan.FromDays(7);

    /// <summary>A redeemed session with no live setup owner and no progress this long is abandoned.</summary>
    internal static readonly TimeSpan AbandonedRedeemedSetupGrace = TimeSpan.FromMinutes(30);

    /// <summary>Progress newer than this from a live owner means setup is still working; refuse Start over.</summary>
    internal static readonly TimeSpan ActiveSetupProgressWindow = TimeSpan.FromMinutes(10);

    // Test seams. Production reads the protected progress file, the OS uptime and the real process table.
    internal Func<Guid, DateTimeOffset, WindowsSetupProgressDocument?> SetupProgressReader { get; set; } = ReadWindowsSetupProgress;
    internal Func<DateTimeOffset> LastBootTimeReader { get; set; } = DefaultLastBootTime;
    internal Func<int, DateTimeOffset, bool> SetupOwnerAliveProbe { get; set; } = IsWindowsSetupOwnerAlive;
    internal Func<Guid, string, DevelopmentLaunchResult>? DevelopmentSetupStarterOverride { get; set; }

    private static DateTimeOffset DefaultLastBootTime() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Restart-required progress is resumable only after Windows booted since it was written.</summary>
    internal static bool IsRestartResumeEligible(
        string state, DateTimeOffset observedAt, DateTimeOffset lastBootAt, DateTimeOffset now) =>
        state == "restart-required" && lastBootAt > observedAt && now - observedAt <= RestartResumeWindow;

    /// <summary>
    /// A redeemed fresh-install session is abandoned when no live process owns its progress and nothing
    /// has been reported for a bounded time. A pending restart keeps its own, longer window.
    /// </summary>
    internal static bool IsAbandonedRedeemedSetup(
        string? progressState,
        DateTimeOffset? progressObservedAt,
        DateTimeOffset redeemedAt,
        DateTimeOffset now,
        bool ownerAlive)
    {
        if (ownerAlive && progressState == "running") return false;
        if (progressState == "restart-required" && progressObservedAt is { } requestedAt)
            return now - requestedAt > RestartResumeWindow;
        var lastActivity = progressObservedAt is { } observed && observed > redeemedAt ? observed : redeemedAt;
        return now - lastActivity >= AbandonedRedeemedSetupGrace;
    }

    private static bool IsFreshInstallSession(LocalOfficeSetupSession session) =>
        !session.AutomaticUpdate && session.RecoveryAction is not ("upgrade" or "repair");

    private static bool SetupStillWorking(WindowsSetupProgressDocument? progress, Guid sessionId, DateTimeOffset now) =>
        // ReadWindowsSetupProgress already turns "running" without a live owner into "failed".
        progress is { State: "running" } && progress.JobId == sessionId && now - progress.ObservedAt < ActiveSetupProgressWindow;

    internal static bool CanStartOverLocalSetup(
        LocalOfficeSetupSession session, WindowsSetupProgressDocument? progress, DateTimeOffset now) =>
        IsFreshInstallSession(session) &&
        session.Status is LocalOfficeSetupSessionStatus.Created or LocalOfficeSetupSessionStatus.Redeemed or
            LocalOfficeSetupSessionStatus.RecoveryRequired or LocalOfficeSetupSessionStatus.Failed &&
        !SetupStillWorking(progress, session.Id, now);

    public async Task<LocalOfficeSetupActionResponse> ContinueLocalSetupAfterRestartAsync(
        Guid sessionId,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var session = await dbContext.LocalOfficeSetupSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.CreatedByUserId == createdByUserId, cancellationToken);
        if (session is null)
            return await LocalFailureAsync("session_not_found", "The local setup session was not found.", cancellationToken);
        if (session.Status == LocalOfficeSetupSessionStatus.Created && session.AdministratorApprovalRequestedAt is not null)
            return new LocalOfficeSetupActionResponse(true, null, "Office setup is already continuing.",
                Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, null, LocalSetupLaunchMethod(session)),
                await GetOnboardingStatusAsync(cancellationToken));
        if (session.Status != LocalOfficeSetupSessionStatus.Redeemed || !IsFreshInstallSession(session))
            return await LocalFailureAsync("resume_not_available",
                "This Office setup is not waiting for a Windows restart.", cancellationToken);
        var progress = SetupProgressReader(session.Id, now);
        if (progress is null || progress.JobId != session.Id ||
            !IsRestartResumeEligible(progress.State, progress.ObservedAt, LastBootTimeReader(), now))
            return await LocalFailureAsync("restart_not_detected",
                "Windows has not restarted since setup asked for it. Restart this computer, then choose Continue setup.",
                cancellationToken);

        // Single-use: only the request that moves this exact Redeemed version back to Created mints the
        // handoff. The previous handoff and enrollment token were already consumed by the stopped run.
        var handoff = Base64Url(RandomNumberGenerator.GetBytes(32));
        var handoffHash = Hash(handoff);
        // Status plus the consumed handoff hash identify this exact session version; every transition
        // that could race (redeem, recovery, cancel) changes one of them.
        var observedHandoffHash = session.HandoffSecretHash;
        var previousEnrollmentId = session.ExecutionNodeEnrollmentId;
        if (dbContext.Database.IsRelational())
        {
            var claimed = await dbContext.LocalOfficeSetupSessions
                .Where(x => x.Id == session.Id && x.Status == LocalOfficeSetupSessionStatus.Redeemed &&
                    x.HandoffSecretHash == observedHandoffHash)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, LocalOfficeSetupSessionStatus.Created)
                    .SetProperty(x => x.HandoffSecretHash, handoffHash)
                    .SetProperty(x => x.SetupReceiptHash, (string?)null)
                    .SetProperty(x => x.ExecutionNodeEnrollmentId, (Guid?)null)
                    .SetProperty(x => x.AdministratorApprovalRequestedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.ErrorCode, (string?)null)
                    .SetProperty(x => x.ErrorMessage, (string?)null)
                    .SetProperty(x => x.ExpiresAt, now.Add(AssistedSetupLifetime))
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            await dbContext.Entry(session).ReloadAsync(cancellationToken);
            if (claimed == 0)
                return new LocalOfficeSetupActionResponse(true, null, "Office setup is already continuing.",
                    Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, null, LocalSetupLaunchMethod(session)),
                    await GetOnboardingStatusAsync(cancellationToken));
        }
        else
        {
            session.Status = LocalOfficeSetupSessionStatus.Created;
            session.HandoffSecretHash = handoffHash;
            session.SetupReceiptHash = null;
            session.ExecutionNodeEnrollmentId = null;
            session.AdministratorApprovalRequestedAt = null;
            session.ErrorCode = null;
            session.ErrorMessage = null;
            session.ExpiresAt = now.Add(AssistedSetupLifetime);
            session.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        await RevokeUnclaimedEnrollmentAsync(previousEnrollmentId, now, cancellationToken);
        await auditWriter.WriteAsync("office.local-setup.restart-resumed", nameof(LocalOfficeSetupSession), session.Id,
            "Authorized continuing assisted Office setup after the Windows restart with a fresh one-use handoff.",
            cancellationToken: cancellationToken);

        var launchUri = LocalSetupLaunchUri(session, handoff);
        var launchMethod = LocalSetupLaunchMethod(session);
        if (launchMethod == "server")
            return await LaunchLocalSetupSessionAsync(session.Id, createdByUserId,
                new LaunchLocalOfficeSetupRequest(launchUri), cancellationToken);
        // Protocol launches need the browser to open the configurator; the UI calls launch with this URI.
        return new LocalOfficeSetupActionResponse(true, null, "Office setup is ready to continue.",
            Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, launchUri, launchMethod),
            await GetOnboardingStatusAsync(cancellationToken));
    }

    public async Task<LocalOfficeSetupActionResponse> CancelLocalSetupSessionAsync(
        Guid sessionId,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var session = await dbContext.LocalOfficeSetupSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId && x.CreatedByUserId == createdByUserId, cancellationToken);
        if (session is null)
            return await LocalFailureAsync("session_not_found", "The local setup session was not found.", cancellationToken);
        if (session.Status is LocalOfficeSetupSessionStatus.Expired or LocalOfficeSetupSessionStatus.Revoked)
            return new LocalOfficeSetupActionResponse(true, null, "This setup has already ended. You can start a new one.",
                Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, null, LocalSetupLaunchMethod(session)),
                await GetOnboardingStatusAsync(cancellationToken));
        if (session.AutomaticUpdate)
            return await LocalFailureAsync("use_cancel_update", "Cancel an Office update from its update controls.", cancellationToken);
        if (!IsFreshInstallSession(session))
            return await LocalFailureAsync("cancel_not_available",
                "Office repair and upgrade sessions cannot be abandoned here.", cancellationToken);
        if (session.Status is LocalOfficeSetupSessionStatus.RemovalInProgress)
            return await LocalFailureAsync("removal_in_progress",
                "Office removal is running. Let it finish or retry it before starting over.", cancellationToken);
        if (session.Status is not (LocalOfficeSetupSessionStatus.Created or LocalOfficeSetupSessionStatus.Redeemed or
            LocalOfficeSetupSessionStatus.RecoveryRequired or LocalOfficeSetupSessionStatus.Failed))
            return await LocalFailureAsync("cancel_not_available",
                "This Office is already connected. Manage it from the Office page instead.", cancellationToken);
        if (SetupStillWorking(SetupProgressReader(session.Id, now), session.Id, now))
            return await LocalFailureAsync("setup_running",
                "Windows setup is still running and reporting progress. Wait for it to finish or stop, then start over.",
                cancellationToken);

        var observedStatus = session.Status;
        var observedHandoffHash = session.HandoffSecretHash;
        var enrollmentId = session.ExecutionNodeEnrollmentId;
        // Replace the handoff hash so no copy of the old handoff can ever be redeemed.
        var deadHandoffHash = Hash(Base64Url(RandomNumberGenerator.GetBytes(32)));
        const string message = "The local Office setup was abandoned by its administrator.";
        if (dbContext.Database.IsRelational())
        {
            var cancelled = await dbContext.LocalOfficeSetupSessions
                .Where(x => x.Id == session.Id && x.Status == observedStatus && x.HandoffSecretHash == observedHandoffHash)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, LocalOfficeSetupSessionStatus.Expired)
                    .SetProperty(x => x.HandoffSecretHash, deadHandoffHash)
                    .SetProperty(x => x.SetupReceiptHash, (string?)null)
                    .SetProperty(x => x.ErrorCode, LocalSetupCancelledCode)
                    .SetProperty(x => x.ErrorMessage, message)
                    .SetProperty(x => x.ExpiresAt, now)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            await dbContext.Entry(session).ReloadAsync(cancellationToken);
            if (cancelled == 0)
                return await LocalFailureAsync("session_changed",
                    "This setup changed while it was being cancelled. Review its current state and try again.", cancellationToken);
        }
        else
        {
            session.Status = LocalOfficeSetupSessionStatus.Expired;
            session.HandoffSecretHash = deadHandoffHash;
            session.SetupReceiptHash = null;
            session.ErrorCode = LocalSetupCancelledCode;
            session.ErrorMessage = message;
            session.ExpiresAt = now;
            session.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        await RevokeUnclaimedEnrollmentAsync(enrollmentId, now, cancellationToken);
        await auditWriter.WriteAsync("office.local-setup.cancelled", nameof(LocalOfficeSetupSession), session.Id,
            $"Abandoned an assisted Windows Office setup session that was {observedStatus.ToString().ToLowerInvariant()}.",
            cancellationToken: cancellationToken);
        return new LocalOfficeSetupActionResponse(true, null, "The setup was cancelled. You can create a fresh Office now.",
            Map(session, fleetOptions?.Value.WindowsPackageOverrideUrl, null, LocalSetupLaunchMethod(session)),
            await GetOnboardingStatusAsync(cancellationToken));
    }

    private async Task ExpireAbandonedRedeemedSetupsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - AbandonedRedeemedSetupGrace;
        var candidates = await dbContext.LocalOfficeSetupSessions.Where(x =>
                x.Status == LocalOfficeSetupSessionStatus.Redeemed && !x.AutomaticUpdate &&
                x.RecoveryAction != "upgrade" && x.RecoveryAction != "repair" &&
                x.RedeemedAt != null && x.RedeemedAt <= cutoff)
            .ToListAsync(cancellationToken);
        foreach (var session in candidates)
        {
            // A claimed enrollment means the Office connected; claim reconciliation owns that session.
            if (session.ExecutionNodeId is not null) continue;
            if (session.ExecutionNodeEnrollmentId is { } enrollmentId && await dbContext.ExecutionNodeEnrollments.AnyAsync(x =>
                    x.Id == enrollmentId && (x.ExecutionNodeId != null ||
                        x.Status == ExecutionEnrollmentStatus.Claimed || x.Status == ExecutionEnrollmentStatus.Approved),
                    cancellationToken))
                continue;
            var progress = SetupProgressReader(session.Id, now);
            if (progress is not null && progress.JobId != session.Id) progress = null;
            var ownerAlive = progress is { OwnerProcessId: > 0 } &&
                SetupOwnerAliveProbe(progress.OwnerProcessId, progress.ObservedAt);
            if (!IsAbandonedRedeemedSetup(progress?.State, progress?.ObservedAt, session.RedeemedAt!.Value, now, ownerAlive))
                continue;
            var restartExpired = progress?.State == "restart-required";
            session.Status = LocalOfficeSetupSessionStatus.Expired;
            session.SetupReceiptHash = null;
            session.ErrorCode = LocalSetupInterruptedCode;
            session.ErrorMessage = restartExpired
                ? "Setup waited too long for the Windows restart. Start again to create a fresh Office setup."
                : "Windows setup stopped without finishing and no setup process is running. Start again to create a fresh Office setup.";
            session.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
            await RevokeUnclaimedEnrollmentAsync(session.ExecutionNodeEnrollmentId, now, cancellationToken);
            await auditWriter.WriteAsync("office.local-setup.interrupted-expired", nameof(LocalOfficeSetupSession), session.Id,
                restartExpired
                    ? "Expired an assisted Office setup whose Windows restart was not completed within the resume window."
                    : "Expired an assisted Office setup whose elevated setup process stopped without reporting progress.",
                cancellationToken: cancellationToken);
        }
    }

    private async Task RevokeUnclaimedEnrollmentAsync(Guid? enrollmentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (enrollmentId is not { } id) return;
        var enrollment = await dbContext.ExecutionNodeEnrollments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        // Never touch a claimed enrollment: it already produced an Office identity.
        if (enrollment?.Status is not (ExecutionEnrollmentStatus.Available or ExecutionEnrollmentStatus.Expired)) return;
        enrollment.Status = ExecutionEnrollmentStatus.Revoked;
        enrollment.RevokedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
