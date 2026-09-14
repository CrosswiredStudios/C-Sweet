using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Wire = CSweet.WorkManagement.Contracts;

namespace CSweet.Infrastructure.WorkManagement;

// The caller must authorize the item through IPersonalTodoService before using this reader.
public sealed class PersonalTodoActivityReader(CSweetDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    public async Task<PersonalTodoActivityResponse> ReadAsync(Guid organizationId, Wire.PersonalTodoItem item, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var installation = await db.CoreOrganizationUsers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Id == item.OwnerOrganizationUserId)
            .Select(x => x.AgentInstallationId).SingleOrDefaultAsync(token);
        var prefix = $"personal-todo-available:{item.Id:N}:";
        var work = installation.HasValue ? await db.AgentWorkItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId.ToString() && x.AgentInstallationId == installation && x.IdempotencyKey.StartsWith(prefix))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(token) : null;
        var attempt = work is null ? null : await db.AgentWorkAttempts.AsNoTracking()
            .Where(x => x.AgentWorkItemId == work.Id && x.Attempt == work.AttemptCount).SingleOrDefaultAsync(token);
        var progress = attempt is null ? null : await db.AgentWorkProgress.AsNoTracking()
            .Where(x => x.AgentWorkAttemptId == attempt.Id).OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(token);
        string? phase = null;
        if (progress is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(protection.CreateProtector("CSweet.AgentWorkInbox.v1").Unprotect(progress.ProtectedValue));
                var value = json.RootElement;
                if (value.TryGetProperty("itemId", out var id) && id.TryGetGuid(out var reportedItem) && reportedItem == item.Id &&
                    value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    phase = message.GetString();
                if (phase?.Length > 500) phase = phase[..500];
            }
            catch (Exception error) when (error is CryptographicException or JsonException or InvalidOperationException) { }
        }
        // Run logs currently identify the installation, not the ticket. Label this explicitly
        // as agent-wide activity; never use it as proof this ticket advanced.
        var inference = installation.HasValue ? await db.AgentRunLogs.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AgentInstallationId == installation && x.StartedAt >= item.UpdatedAt)
            .OrderByDescending(x => x.StartedAt).Select(x => new { x.Status, x.StartedAt, x.CompletedAt }).FirstOrDefaultAsync(token) : null;
        var (state, explanation) = Describe(item.Status, installation.HasValue, item.Wait?.Reason, work?.Status,
            attempt?.FinishedAt is null ? attempt?.LeaseExpiresAt : null, progress?.OccurredAt, now);
        return new(item.Status, state, explanation, now, phase, progress?.OccurredAt,
            attempt?.FinishedAt is null ? attempt?.LeaseExpiresAt : null, work?.AttemptCount ?? 0, work?.MaximumAttempts ?? 0,
            item.Wait?.NextReviewAt, inference?.Status, inference?.StartedAt, inference?.CompletedAt);
    }

    internal static (string State, string Message) Describe(string status, bool hasAgent, string? waiting,
        AgentWorkStatus? workStatus, DateTimeOffset? lease, DateTimeOffset? progress, DateTimeOffset now)
    {
        if (status == Wire.PersonalTodoStatuses.Completed) return ("Completed", "This ticket is complete.");
        if (status == Wire.PersonalTodoStatuses.Blocked) return ("Blocked", "This ticket needs attention. Review its blocker below.");
        if (waiting is not null) return ("Waiting", waiting);
        if (!hasAgent) return ("Manual", "This ticket belongs to a person; no agent is executing it.");
        if (status == Wire.PersonalTodoStatuses.Backlog) return ("Backlog", "This ticket is not queued for execution.");
        if (workStatus == AgentWorkStatus.Leased && lease > now)
        {
            if (progress is null || now - progress.Value > TimeSpan.FromMinutes(5))
                return ("Progress unconfirmed", "The worker lease is current, but no task progress was reported in the last five minutes. It may be working or stalled; a heartbeat alone does not prove progress.");
            return ("Working", "The worker lease is current and task progress was recently reported.");
        }
        if (status == Wire.PersonalTodoStatuses.Running || workStatus == AgentWorkStatus.Leased)
            return ("Recovering", "The execution lease is no longer current. Waiting for automatic recovery; active work is not confirmed.");
        if (workStatus == AgentWorkStatus.DeadLetter) return ("Needs attention", "Automatic execution attempts are exhausted. The ticket is awaiting reconciliation to Blocked.");
        return ("Queued", "Waiting for the agent to claim this ticket. Execution has not started.");
    }
}
