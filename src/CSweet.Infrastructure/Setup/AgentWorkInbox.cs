using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Domain.Communications;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Application.Setup;
using CSweet.Contracts.Plugins;
using CSweet.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Setup;

public sealed record ClaimedAgentWork(
    Guid WorkId,
    int Attempt,
    AgentWorkKind Kind,
    string Name,
    JsonElement Payload,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset Deadline,
    Guid? EventId,
    string CorrelationId);

public sealed record AgentWorkCompletion(bool Succeeded, JsonElement? Value, string? Error);
public sealed record AgentWorkProgressValue(long Sequence, JsonElement Value);
public sealed record AgentWorkState(AgentWorkStatus Status, AgentWorkCompletion? Completion, string? Error);

public sealed class AgentWorkInbox(
    CSweetDbContext db,
    IDataProtectionProvider protectionProvider,
    TimeProvider timeProvider,
    IAuditEventWriter? audit = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private const int MaximumPayloadBytes = 256 * 1024;
    private const int MaximumProgressBytes = 64 * 1024;
    private const int MaximumResultBytes = 1024 * 1024;
    private const int MaximumQueuedWorkPerInstallation = 1000;
    private const int MaximumProgressRecordsPerWork = 1000;
    private readonly IDataProtector _protector =
        protectionProvider.CreateProtector("CSweet.AgentWorkInbox.v1");

    public async Task<AgentWorkItem> EnqueueAsync(
        string organizationId,
        Guid installationId,
        AgentWorkKind kind,
        string name,
        JsonElement payload,
        string idempotencyKey,
        DateTimeOffset deadline,
        string? correlationId = null,
        string? causationId = null,
        string? sourceType = null,
        string? sourceId = null,
        int maximumAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length > MaximumPayloadBytes)
            throw new InvalidOperationException($"Agent work payloads may not exceed {MaximumPayloadBytes} bytes.");
        if (deadline <= timeProvider.GetUtcNow())
            throw new InvalidOperationException("Agent work must have a future deadline.");
        if (kind == AgentWorkKind.Event &&
            (!Guid.TryParse(sourceId, out var eventId) || eventId == Guid.Empty))
            throw new InvalidOperationException(
                "Event work requires the originating event ID.");

        var existing = await db.AgentWorkItems.SingleOrDefaultAsync(
            x => x.AgentInstallationId == installationId &&
                 x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null &&
            existing.PayloadHash == payloadHash &&
            (kind != AgentWorkKind.Event ||
             string.Equals(existing.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)))
            return existing;
        if (existing is not null)
            throw new InvalidOperationException(
                "The idempotency key is already bound to different work content.");

        var installation = await db.AgentInstallations.AsNoTracking().SingleAsync(
            x => x.Id == installationId && x.IsEnabled && x.BusinessId == organizationId,
            cancellationToken);
        if (installation.SetupState != PluginSetupState.Ready)
        {
            var manifestJson = await db.AgentPackageVersions.AsNoTracking()
                .Where(x => x.Id == installation.PackageVersionId).Select(x => x.ManifestJson)
                .SingleOrDefaultAsync(cancellationToken);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson ?? "{}", JsonOptions)
                ?? throw new InvalidOperationException("The plugin manifest is unavailable during setup.");
            var isBootstrap = kind == AgentWorkKind.Capability && sourceType == "plugin-setup" &&
                manifest.Provides.Any(x => x.Name == name && x.RiskClass == "bootstrap");
            if (!isBootstrap)
                throw new InvalidOperationException("Only manifest-declared bootstrap callbacks may run before setup completes.");
        }
        var queueDepth = await db.AgentWorkItems.CountAsync(
            x => x.AgentInstallationId == installationId &&
                 (x.Status == AgentWorkStatus.Pending || x.Status == AgentWorkStatus.Leased),
            cancellationToken);
        if (queueDepth >= MaximumQueuedWorkPerInstallation)
            throw new InvalidOperationException(
                $"The installation queue limit of {MaximumQueuedWorkPerInstallation} work items was reached.");
        var now = timeProvider.GetUtcNow();
        var item = new AgentWorkItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = installation.BusinessId,
            AgentInstallationId = installation.Id,
            Kind = kind,
            Name = name,
            ProtectedPayload = _protector.Protect(bytes),
            PayloadHash = payloadHash,
            CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
            CausationId = causationId,
            SourceType = sourceType,
            SourceId = sourceId,
            IdempotencyKey = idempotencyKey,
            AvailableAt = now,
            DeadlineAt = deadline,
            MaximumAttempts = Math.Clamp(maximumAttempts, 1, 10),
            CreatedAt = now
        };
        db.AgentWorkItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        AgentRuntimeMetrics.Work("enqueued", kind);
        return item;
    }

    public async Task<ClaimedAgentWork?> ClaimAsync(
        McpAgentSession session,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var expiredPending = await db.AgentWorkItems
            .Where(x =>
                x.OrganizationId == session.OrganizationId &&
                x.AgentInstallationId == session.AgentInstallationId &&
                x.Status == AgentWorkStatus.Pending &&
                x.DeadlineAt <= now)
            .ToListAsync(cancellationToken);
        foreach (var expiredItem in expiredPending)
        {
            expiredItem.Status = AgentWorkStatus.DeadLetter;
            expiredItem.LastError = "The work deadline elapsed before the item could be claimed.";
            await FailCoordinationForWorkAsync(expiredItem, expiredItem.LastError, now, cancellationToken);
            AgentRuntimeMetrics.Work("dead_lettered", expiredItem.Kind);
        }

        var expired = await db.AgentWorkAttempts
            .Include(x => x.AgentWorkItem)
            .Where(x => x.FinishedAt == null &&
                        x.LeaseExpiresAt <= now &&
                        x.AgentWorkItem!.Status == AgentWorkStatus.Leased)
            .ToListAsync(cancellationToken);
        foreach (var expiredAttempt in expired)
        {
            expiredAttempt.FinishedAt = now;
            expiredAttempt.Error = "lease_expired";
            var work = expiredAttempt.AgentWorkItem!;
            work.Status = work.AttemptCount >= work.MaximumAttempts
                ? AgentWorkStatus.DeadLetter
                : AgentWorkStatus.Pending;
            work.AvailableAt = now;
            work.LastError = "The prior runtime lease expired.";
            if (work.Status == AgentWorkStatus.DeadLetter)
                await FailCoordinationForWorkAsync(work, work.LastError, now, cancellationToken);
            AgentRuntimeMetrics.Work(
                work.Status == AgentWorkStatus.DeadLetter ? "dead_lettered" : "requeued",
                work.Kind);
        }

        var item = await db.AgentWorkItems
            .Where(x => x.OrganizationId == session.OrganizationId &&
                        x.AgentInstallationId == session.AgentInstallationId &&
                        x.Status == AgentWorkStatus.Pending &&
                        x.AvailableAt <= now &&
                        x.DeadlineAt > now)
            .OrderBy(x => x.Kind == AgentWorkKind.ConfigurationUpdate ? 0 : 1)
            .ThenBy(x => x.AvailableAt)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var leaseToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        item.Status = AgentWorkStatus.Leased;
        item.AttemptCount++;
        var attempt = new AgentWorkAttempt
        {
            Id = Guid.NewGuid(),
            AgentWorkItemId = item.Id,
            RuntimeInstanceId = session.RuntimeInstanceId,
            Attempt = item.AttemptCount,
            LeaseTokenHash = Hash(leaseToken),
            ClaimedAt = now,
            LeaseExpiresAt = now.Add(LeaseDuration)
        };
        db.AgentWorkAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        AgentRuntimeMetrics.WorkClaimed(item.Kind, now - item.CreatedAt);

        var payload = JsonDocument.Parse(_protector.Unprotect(item.ProtectedPayload)).RootElement.Clone();
        return new ClaimedAgentWork(
            item.Id,
            attempt.Attempt,
            item.Kind,
            item.Name,
            payload,
            leaseToken,
            attempt.LeaseExpiresAt,
            item.DeadlineAt,
            item.Kind == AgentWorkKind.Event
                ? Guid.Parse(item.SourceId!)
                : null,
            item.CorrelationId);
    }

    public async Task<DateTimeOffset> RenewAsync(
        McpAgentSession session,
        Guid workId,
        int attemptNumber,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        var attempt = await GetActiveAttemptAsync(
            session, workId, attemptNumber, leaseToken, cancellationToken);
        var now = timeProvider.GetUtcNow();
        attempt.LeaseExpiresAt = now.Add(LeaseDuration);
        await db.SaveChangesAsync(cancellationToken);
        AgentRuntimeMetrics.Work("lease_renewed", attempt.AgentWorkItem!.Kind);
        return attempt.LeaseExpiresAt;
    }

    public async Task AppendProgressAsync(
        McpAgentSession session,
        Guid workId,
        int attemptNumber,
        string leaseToken,
        long sequence,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumProgressBytes)
            throw new InvalidOperationException($"Progress values may not exceed {MaximumProgressBytes} bytes.");
        var attempt = await GetActiveAttemptAsync(
            session, workId, attemptNumber, leaseToken, cancellationToken);
        if (sequence != attempt.LastProgressSequence + 1)
            throw new InvalidOperationException("Progress sequence numbers must be strictly monotonic without gaps.");
        if (sequence > MaximumProgressRecordsPerWork)
            throw new InvalidOperationException(
                $"Work items may not emit more than {MaximumProgressRecordsPerWork} progress records.");
        attempt.LastProgressSequence = sequence;
        db.AgentWorkProgress.Add(new AgentWorkProgress
        {
            Id = Guid.NewGuid(),
            AgentWorkItemId = workId,
            AgentWorkAttemptId = attempt.Id,
            Sequence = sequence,
            ProtectedValue = _protector.Protect(bytes),
            SizeBytes = bytes.Length,
            OccurredAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        AgentRuntimeMetrics.Work("progressed", attempt.AgentWorkItem!.Kind);
    }

    public async Task CompleteAsync(
        McpAgentSession session,
        Guid workId,
        int attemptNumber,
        string leaseToken,
        AgentWorkCompletion completion,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(completion);
        if (bytes.Length > MaximumResultBytes)
            throw new InvalidOperationException($"Agent work results may not exceed {MaximumResultBytes} bytes.");
        var completionHash = Convert.ToHexString(SHA256.HashData(bytes));
        var item = await db.AgentWorkItems.Include(x => x.Attempts)
            .SingleAsync(x => x.Id == workId, cancellationToken);
        var attempt = item.Attempts.SingleOrDefault(x => x.Attempt == attemptNumber)
            ?? throw new InvalidOperationException("The work attempt does not exist.");
        if (attempt.FinishedAt is not null)
        {
            if (attempt.CompletionHash == completionHash)
                return;
            throw new InvalidOperationException("The work attempt was already completed with different content.");
        }
        ValidateLease(session, item, attempt, leaseToken);
        var now = timeProvider.GetUtcNow();
        attempt.FinishedAt = now;
        attempt.CompletionHash = completionHash;
        item.ProtectedResult = _protector.Protect(bytes);
        item.ResultHash = completionHash;
        item.Status = AgentWorkStatus.Completed;
        item.CompletedAt = now;
        item.LastError = completion.Succeeded ? null : completion.Error;
        if (item.Kind == AgentWorkKind.ConfigurationUpdate)
            ApplyConfigurationAcknowledgement(item, completion, now);
        await db.SaveChangesAsync(cancellationToken);
        if (item.Kind == AgentWorkKind.ConfigurationUpdate && audit is not null)
            await audit.WriteAsync(
                "agent-installation.configuration.refresh-acknowledged",
                nameof(AgentInstallation),
                item.AgentInstallationId,
                $"Configuration refresh work {item.Id:D} completed with status {item.Status}.",
                cancellationToken: cancellationToken);
        AgentRuntimeMetrics.Work("completed", item.Kind);
    }

    public async Task FailAsync(
        McpAgentSession session,
        Guid workId,
        int attemptNumber,
        string leaseToken,
        string error,
        CancellationToken cancellationToken)
    {
        var attempt = await GetActiveAttemptAsync(
            session, workId, attemptNumber, leaseToken, cancellationToken);
        var item = attempt.AgentWorkItem!;
        var now = timeProvider.GetUtcNow();
        attempt.FinishedAt = now;
        attempt.Error = Truncate(error, 2048);
        item.LastError = attempt.Error;
        item.Status = item.AttemptCount >= item.MaximumAttempts || item.DeadlineAt <= now
            ? AgentWorkStatus.DeadLetter
            : AgentWorkStatus.Pending;
        item.AvailableAt = now.AddSeconds(Math.Min(60, Math.Pow(2, attemptNumber)));
        if (item.Kind == AgentWorkKind.ConfigurationUpdate)
        {
            var installation = await db.AgentInstallations.SingleAsync(x => x.Id == item.AgentInstallationId,
                cancellationToken);
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.Restarting;
            installation.ConfigurationSyncLastError = Truncate(error, 2048);
            AgentRuntimeMetrics.ConfigurationRefreshFailed();
        }
        if (item.Status == AgentWorkStatus.DeadLetter)
            await FailCoordinationForWorkAsync(item, item.LastError, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        AgentRuntimeMetrics.Work(
            item.Status == AgentWorkStatus.DeadLetter ? "dead_lettered" : "failed",
            item.Kind);
    }

    private void ApplyConfigurationAcknowledgement(
        AgentWorkItem item,
        AgentWorkCompletion completion,
        DateTimeOffset now)
    {
        var installation = db.AgentInstallations.Local.FirstOrDefault(x => x.Id == item.AgentInstallationId)
            ?? db.AgentInstallations.Single(x => x.Id == item.AgentInstallationId);
        if (!completion.Succeeded || completion.Value is not { } value ||
            !value.TryGetProperty("appliedRevision", out var revisionElement) ||
            !revisionElement.TryGetInt64(out var revision))
        {
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.Restarting;
            installation.ConfigurationSyncLastError = completion.Error ?? "The configuration refresh acknowledgment was invalid.";
            AgentRuntimeMetrics.ConfigurationRefreshFailed();
            return;
        }
        var restartRequired = value.TryGetProperty("result", out var resultElement) &&
            string.Equals(resultElement.GetString(), "RestartRequired", StringComparison.Ordinal);
        installation.AppliedConfigurationRevision = Math.Max(installation.AppliedConfigurationRevision, revision);
        if (revision < installation.DesiredConfigurationRevision)
        {
            installation.ConfigurationSyncStatus = AgentConfigurationSyncStatus.Refreshing;
            installation.ConfigurationSyncLastError = null;
            AgentRuntimeMetrics.ConfigurationDriftDetected();
            return;
        }
        installation.ConfigurationSyncStatus = restartRequired
            ? AgentConfigurationSyncStatus.Restarting : AgentConfigurationSyncStatus.Current;
        installation.ConfigurationSyncLastError = null;
        if (restartRequired)
            AgentRuntimeMetrics.ConfigurationRestartFallback();
        else if (installation.ConfigurationSyncLastAttemptAt is { } attempted)
            AgentRuntimeMetrics.ConfigurationRefreshSucceeded(now - attempted);
    }

    public async Task<T> WaitForResultAsync<T>(
        Guid workId,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            db.ChangeTracker.Clear();
            var item = await db.AgentWorkItems.AsNoTracking()
                .SingleAsync(x => x.Id == workId, cancellationToken);
            if (item.Status == AgentWorkStatus.Completed && item.ProtectedResult is not null)
            {
                var completion = JsonSerializer.Deserialize<AgentWorkCompletion>(
                    _protector.Unprotect(item.ProtectedResult))
                    ?? throw new InvalidOperationException("The agent work result could not be decoded.");
                if (!completion.Succeeded)
                    throw new InvalidOperationException(completion.Error ?? "Agent work failed.");
                if (completion.Value is not { } value)
                    throw new InvalidOperationException("The agent work result was empty.");
                var result = value.Deserialize<T>();
                return result is not null
                    ? result
                    : throw new InvalidOperationException("The agent work result was empty.");
            }
            if (item.Status is AgentWorkStatus.Cancelled or AgentWorkStatus.DeadLetter)
                throw new InvalidOperationException(item.LastError ?? $"Agent work ended as {item.Status}.");
            if (item.DeadlineAt <= timeProvider.GetUtcNow())
                throw new TimeoutException("The agent work deadline elapsed.");
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<AgentWorkProgressValue>> ReadProgressAfterAsync(
        Guid workId,
        long sequence,
        CancellationToken cancellationToken)
    {
        var records = await db.AgentWorkProgress.AsNoTracking()
            .Where(x => x.AgentWorkItemId == workId && x.Sequence > sequence)
            .OrderBy(x => x.Sequence)
            .Take(100)
            .ToListAsync(cancellationToken);
        return records.Select(x => new AgentWorkProgressValue(
            x.Sequence,
            JsonDocument.Parse(_protector.Unprotect(x.ProtectedValue)).RootElement.Clone()))
            .ToList();
    }

    public async Task<AgentWorkState> ReadStateAsync(
        Guid workId,
        CancellationToken cancellationToken)
    {
        var item = await db.AgentWorkItems.AsNoTracking()
            .SingleAsync(x => x.Id == workId, cancellationToken);
        AgentWorkCompletion? completion = null;
        if (item.ProtectedResult is not null)
            completion = JsonSerializer.Deserialize<AgentWorkCompletion>(
                _protector.Unprotect(item.ProtectedResult));
        return new AgentWorkState(item.Status, completion, item.LastError);
    }

    public async Task<bool> CancelAsync(
        Guid workId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var item = await db.AgentWorkItems.SingleOrDefaultAsync(x => x.Id == workId, cancellationToken);
        if (item is null || item.Status is AgentWorkStatus.Completed or AgentWorkStatus.Cancelled or AgentWorkStatus.DeadLetter)
            return false;
        item.Status = AgentWorkStatus.Cancelled;
        item.CompletedAt = timeProvider.GetUtcNow();
        item.LastError = Truncate(reason, 2048);
        var activeAttempts = await db.AgentWorkAttempts.Where(x =>
            x.AgentWorkItemId == workId && x.FinishedAt == null).ToListAsync(cancellationToken);
        foreach (var attempt in activeAttempts)
        {
            attempt.FinishedAt = item.CompletedAt;
            attempt.Error = "cancelled";
        }
        await db.SaveChangesAsync(cancellationToken);
        AgentRuntimeMetrics.Work("cancelled", item.Kind);
        return true;
    }

    public async Task<int> CancelBySourceAsync(
        string sourceType,
        string sourceId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var items = await db.AgentWorkItems
            .Include(x => x.Attempts)
            .Where(x => x.SourceType == sourceType && x.SourceId == sourceId &&
                x.Status != AgentWorkStatus.Completed &&
                x.Status != AgentWorkStatus.Cancelled &&
                x.Status != AgentWorkStatus.DeadLetter)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
            return 0;

        var now = timeProvider.GetUtcNow();
        foreach (var item in items)
        {
            item.Status = AgentWorkStatus.Cancelled;
            item.CompletedAt = now;
            item.LastError = Truncate(reason, 2048);
            foreach (var attempt in item.Attempts.Where(x => x.FinishedAt == null))
            {
                attempt.FinishedAt = now;
                attempt.Error = "cancelled";
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var item in items)
            AgentRuntimeMetrics.Work("cancelled", item.Kind);
        return items.Count;
    }

    private async Task FailCoordinationForWorkAsync(
        AgentWorkItem item,
        string? error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(item.SourceType, "agent-coordination", StringComparison.Ordinal) ||
            !Guid.TryParse(item.CorrelationId, out var sessionId))
            return;
        var session = await db.AgentCoordinationSessions.SingleOrDefaultAsync(x =>
            x.Id == sessionId &&
            (x.Status == AgentCoordinationStatus.Active ||
             x.Status == AgentCoordinationStatus.Summarizing), cancellationToken);
        if (session is null) return;

        var detail = string.IsNullOrWhiteSpace(error)
            ? "The assigned agent work could not be completed."
            : Truncate(error, 1000);
        session.Status = AgentCoordinationStatus.Failed;
        session.Revision++;
        session.CurrentOrganizationUserId = null;
        session.CurrentAgentWorkItemId = null;
        session.CompletedAt = session.UpdatedAt = now;
        session.FinalSummary = $"Collaboration failed because an agent turn could not continue: {detail}";
        var summaryKey = $"coordination:{session.Id:N}:summary";
        if (!await db.CoreConversationMessages.AnyAsync(x =>
                x.ConversationId == session.SourceConversationId &&
                x.IdempotencyKey == summaryKey, cancellationToken))
        {
            db.CoreConversationMessages.Add(new ConversationMessage
            {
                Id = Guid.NewGuid(), ConversationId = session.SourceConversationId,
                CoordinationSessionId = session.Id,
                SenderOrganizationUserId = session.InitiatorOrganizationUserId,
                Role = ConversationRole.Assistant,
                Content = session.FinalSummary,
                CorrelationId = session.Id,
                DeliveryIntent = CommunicationDeliveryIntent.Response,
                SourceProvider = "InApp",
                IdempotencyKey = summaryKey,
                CreatedAt = now
            });
        }
    }

    private async Task<AgentWorkAttempt> GetActiveAttemptAsync(
        McpAgentSession session,
        Guid workId,
        int attemptNumber,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        var attempt = await db.AgentWorkAttempts.Include(x => x.AgentWorkItem)
            .SingleOrDefaultAsync(
                x => x.AgentWorkItemId == workId && x.Attempt == attemptNumber,
                cancellationToken)
            ?? throw new InvalidOperationException("The work attempt does not exist.");
        ValidateLease(session, attempt.AgentWorkItem!, attempt, leaseToken);
        return attempt;
    }

    private void ValidateLease(
        McpAgentSession session,
        AgentWorkItem item,
        AgentWorkAttempt attempt,
        string leaseToken)
    {
        var now = timeProvider.GetUtcNow();
        if (attempt.RuntimeInstanceId != session.RuntimeInstanceId ||
            item.AgentInstallationId != session.AgentInstallationId ||
            item.OrganizationId != session.OrganizationId ||
            item.Status != AgentWorkStatus.Leased ||
            attempt.FinishedAt is not null ||
            attempt.LeaseExpiresAt <= now ||
            item.DeadlineAt <= now ||
            !FixedHashEquals(attempt.LeaseTokenHash, Hash(leaseToken)))
            throw new UnauthorizedAccessException("The work lease is stale, expired, forged, or bound to another runtime.");
    }

    private static bool FixedHashEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
