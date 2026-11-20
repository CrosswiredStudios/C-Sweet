using CSweet.Application.Setup;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using HiringRecommendationResponse = CSweet.Contracts.Core.HiringRecommendationResponse;
using CSweet.Contracts.Realtime;
using CSweet.Domain.Core;
using CSweet.Infrastructure.WorkManagement;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private const string HiringDependencyOperation = "automatic-hiring";

    private async Task<DelegatedHireResponse?> PendingHiringDependencyAsync(Guid org, Guid installation, Guid recommendation,
        CancellationToken ct)
    {
        var row = await DependencyWaits.FindAsync(db, org, installation, HiringDependencyOperation, recommendation, ct);
        if (row is null) return null;
        // A changed owner selection mode is material input, not an unchanged retry.
        var policy = await ReadPolicyAsync(org, installation, ct);
        if (!policy.SetupComplete || policy.Settings.Mode != HiringSelectionMode.Automatic) return null;
        var state = DependencyWaits.Read(row);
        return state.Status == "NeedsReview" ? new(recommendation, null, "NeedsReview", state.Message)
            : state.Status == "WaitingDependency" && state.RetryAt > DateTimeOffset.UtcNow
                ? new(recommendation, null, "Queued", state.Message) : null;
    }

    private async Task<DelegatedHireResponse> RecordHiringFailureAsync(Guid org, Guid installation, Guid recommendation,
        Exception error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = await DependencyWaits.FindAsync(db, org, installation, HiringDependencyOperation, recommendation, ct);
        var attempts = previous is null ? 1 : DependencyWaits.Read(previous).Attempts + 1;
        var transient = error is AgentImportPreviewException { IsDependencyFailure: true, RequiresOwnerAction: false } or HttpRequestException;
        var retryAt = transient ? (error as AgentImportPreviewException)?.RetryAt ?? now.AddSeconds(Math.Min(900, 30 * Math.Pow(2, Math.Min(attempts - 1, 5)))) : (DateTimeOffset?)null;
        if (retryAt <= now) retryAt = now.AddSeconds(30);
        var code = error is AgentImportPreviewException { IsRateLimited: true } ? "dependency.rate_limited"
            : transient ? "dependency.unavailable" : "hiring.review_required";
        await DependencyWaits.SaveAsync(db, org, installation, new(HiringDependencyOperation, recommendation,
            transient ? "WaitingDependency" : "NeedsReview", code, error.Message, retryAt, attempts), now, ct);
        var card = await ChiefWorkCards.ActionAsync(db, org, installation, $"hire:{recommendation:N}",
            "Complete automatic hiring", ct);
        // A scheduled retry keeps this passive personal card in progress until the worker resumes it.
        if (card is not null) await ChiefWorkCards.FinishActionAsync(db, card,
            transient ? WorkTaskStatus.Running : WorkTaskStatus.Blocked, error.Message, ct);
        BusinessSuggestionService.Queue(db, org, AppRealtimeEvents.HiringRecommendationsChanged, recommendation);
        await db.SaveChangesAsync(ct);
        return new(recommendation, null, transient ? "Queued" : "NeedsReview", error.Message);
    }

    private async Task SettleHiringDependencyAsync(Guid org, Guid installation, Guid recommendation, string status,
        string? message, CancellationToken ct)
    {
        var row = await DependencyWaits.FindAsync(db, org, installation, HiringDependencyOperation, recommendation, ct);
        if (row is null && status is not ("NeedsReview" or "Failed")) return;
        var value = row is null ? new DependencyWait(HiringDependencyOperation, recommendation, status,
            "hiring.review_required", message ?? "Hiring requires owner review.", null, 1) : DependencyWaits.Read(row);
        await DependencyWaits.SaveAsync(db, org, installation, value with { Status = status, Message = message ?? "",
            Code = status is "NeedsReview" or "Failed" ? "hiring.review_required" : "", RetryAt = null }, DateTimeOffset.UtcNow, ct);
        BusinessSuggestionService.Queue(db, org, AppRealtimeEvents.HiringRecommendationsChanged, recommendation);
    }

    public async Task<HiringRecommendationResponse?> RetryAutomaticHiringAsync(Guid org, Guid recommendation, Guid user,
        CancellationToken ct = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockHiringOrganizationAsync(org, ct);
        _ = await RequireOwnerAsync(org, user, ct);
        var plan = await db.WorkforcePlans.SingleOrDefaultAsync(x => x.OrganizationId == org && x.Id == recommendation, ct);
        if (plan is null) return null;
        if (plan.ManualSelectionRequestedAt.HasValue)
            throw new InvalidOperationException("Automatic hiring is stopped for this role. Use the hiring review to choose and approve the candidate.");
        if (plan.Status == ProposalStatus.Pending && plan.FulfilledHeadcount < plan.Headcount)
        {
            var wait = await DependencyWaits.FindAsync(db, org, plan.RequestingInstallationId, HiringDependencyOperation, recommendation, ct);
            if (wait is not null && DependencyWaits.Read(wait).Status == "NeedsReview")
                await SettleHiringDependencyAsync(org, plan.RequestingInstallationId, recommendation, "Retrying", null, ct);
            _ = await SubmitDelegatedAsync(org, plan.RequestingInstallationId, recommendation, ct);
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return (await ListRecommendationsAsync(org, ct)).SingleOrDefault(x => x.Id == recommendation) ?? ToRecommendation(plan, []);
    }

    // Bounded host discovery recovers a persisted due wait after restarts or missed wake hints.
    // It does not run an agent/model review or reuse a prior execution grant.
    public async Task<bool> RecoverNextHiringDependencyAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.PluginOperationalStates.AsNoTracking().Where(x => x.Kind == DependencyWaits.Kind &&
            x.ExternalKey.StartsWith(HiringDependencyOperation + ":") && x.AvailableAt <= now)
            .OrderBy(x => x.AvailableAt).FirstOrDefaultAsync(ct);
        if (row is null) return false;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockHiringOrganizationAsync(row.OrganizationId, ct);
        row = await db.PluginOperationalStates.SingleAsync(x => x.Id == row.Id, ct);
        if (row.AvailableAt is null || row.AvailableAt > DateTimeOffset.UtcNow) return false;
        var value = DependencyWaits.Read(row);
        var plan = await db.WorkforcePlans.SingleOrDefaultAsync(x => x.OrganizationId == row.OrganizationId && x.Id == value.OperationId, ct);
        if (plan is null || plan.RequestingInstallationId != row.AgentInstallationId || plan.ManualSelectionRequestedAt.HasValue || plan.Status != ProposalStatus.Pending ||
            plan.FulfilledHeadcount >= plan.Headcount || !await db.CoreOrganizationUsers.AnyAsync(x => x.OrganizationId == row.OrganizationId &&
                x.AgentInstallationId == row.AgentInstallationId && x.IsActive && x.ArchivedAt == null &&
                db.LeadershipAssignments.Any(a => a.OrganizationId == row.OrganizationId && a.OrganizationUserId == x.Id &&
                    a.PositionKey == LeadershipPositionKeys.ChiefOfStaff && a.EndsAt == null), ct))
            await SettleHiringDependencyAsync(row.OrganizationId, row.AgentInstallationId, value.OperationId, "Cancelled", "This hiring wait is no longer active.", ct);
        else
            _ = await SubmitDelegatedAsync(row.OrganizationId, row.AgentInstallationId, value.OperationId, ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }
}
