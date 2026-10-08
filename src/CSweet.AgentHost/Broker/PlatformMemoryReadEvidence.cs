using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Llm;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.AgentHost.Broker;

public sealed record MemoryReadInvocation(Guid WorkId, int Attempt, Guid RuntimeId, Guid EmployeeId, string AuthorityHash);

public interface IPlatformMemoryReadEvidence
{
    Task<MemoryReadInvocation> BeginAsync(AgentSession session, string capability, CancellationToken token);
    Task RecordAsync(AgentSession session, string capability, MemoryReadInvocation invocation, object? result,
        MemoryPartition? searchPartition, CancellationToken token);
}

public sealed class PlatformMemoryReadEvidence(CSweetDbContext db) : IPlatformMemoryReadEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static UnauthorizedAccessException Denied() => new("The memory read no longer has an authorized work context.");

    public async Task<MemoryReadInvocation> BeginAsync(AgentSession session, string capability, CancellationToken token)
    {
        if (!Guid.TryParse(session.InstallationId, out var installation) || !Guid.TryParse(session.RuntimeInstanceId, out var runtime) ||
            !Guid.TryParse(session.TickId, out var tick) || !Guid.TryParse(session.BusinessId, out var organization) ||
            !session.Grant.RequestedCapabilities.Contains(capability)) throw Denied();
        var grant = await db.AgentInstallations.AsNoTracking().Where(x => x.Id == installation && x.BusinessId == session.BusinessId && x.IsEnabled)
            .Select(x => x.Grant).SingleOrDefaultAsync(token);
        if (grant is null || grant.GrantRevision != session.Grant.Revision ||
            JsonSerializer.Deserialize<string[]>(grant.RequiredCapabilitiesJson)?.Contains(capability, StringComparer.Ordinal) != true) throw Denied();
        var now = DateTimeOffset.UtcNow;
        if (!await db.AgentRuntimeInstances.AsNoTracking().AnyAsync(x => x.Id == runtime && x.TickId == tick && x.AgentInstallationId == installation &&
            x.MemoryReadEvidenceVersion == AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion && x.MemoryResetRequestedAt == null && x.Status == AgentRuntimeStatus.Running && (x.RuntimeDeadlineAt == null || x.RuntimeDeadlineAt > now), token)) throw Denied();
        var identity = await new AgentMemoryIdentityResolver(db).ResolveAsync(session, token) ?? throw Denied();
        session.MemoryTenantId = identity.TenantId; session.MemoryEmployeeId = identity.EmployeeId;
        var leases = await (from attempt in db.AgentWorkAttempts.AsNoTracking()
            join work in db.AgentWorkItems.AsNoTracking() on attempt.AgentWorkItemId equals work.Id
            where attempt.RuntimeInstanceId == runtime && attempt.FinishedAt == null && attempt.LeaseExpiresAt > now &&
                work.Status == AgentWorkStatus.Leased && work.DeadlineAt > now && work.AgentInstallationId == installation && work.OrganizationId == session.BusinessId
            select new { work.Id, attempt.Attempt }).Take(2).ToListAsync(token);
        if (leases.Count != 1) throw Denied();
        var employee = Guid.Parse(identity.EmployeeId);
        return new(leases[0].Id, leases[0].Attempt, runtime, employee, await AuthorityHashAsync(organization, employee, [], token));
    }

    public async Task RecordAsync(AgentSession session, string capability, MemoryReadInvocation invocation, object? result,
        MemoryPartition? searchPartition, CancellationToken token)
    {
        try { await RecordCoreAsync(session, capability, invocation, result, searchPartition, token); }
        catch (MemoryRuntimeResetRequiredException reset)
        {
            await RequestResetAsync(session, reset.ReasonCode, token, reset.ValidationDiagnostic);
            throw Denied();
        }
    }

    private async Task RecordCoreAsync(AgentSession session, string capability, MemoryReadInvocation invocation, object? result,
        MemoryPartition? searchPartition, CancellationToken token)
    {
        var capture = await new MemoryRecallDispatchEvidence(db).CaptureReadAsync(result, searchPartition, token);
        if (await BeginAsync(session, capability, token) != invocation) throw Denied();
        if (capture is null) return;
        if (!db.Database.IsNpgsql()) throw Denied();
        var organization = Guid.Parse(session.BusinessId); var installation = Guid.Parse(session.InstallationId);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var lockKey = $"memory-read:{invocation.RuntimeId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey},0))", token);
        if (await BeginAsync(session, capability, token) != invocation) throw Denied();
        var resolver = new AgentMemoryIdentityResolver(db);
        foreach (var partition in capture.Partitions) await resolver.AuthorizeAsync(session, partition, PlatformMemoryAction.Read, token);
        await RequireSharedReadConsumerAsync(invocation.WorkId, organization, invocation.EmployeeId, capture.EvidenceJson, capture.Partitions, token);
        var authority = await AuthorityHashAsync(organization, invocation.EmployeeId, capture.Partitions, token);
        var fingerprint = Hash(JsonSerializer.Serialize(new { invocation.WorkId, invocation.Attempt, capability, authority, capture.EvidenceJson }, Json));
        if (!await db.AgentMemoryReadReceipts.AnyAsync(x => x.RuntimeId == invocation.RuntimeId && x.ReceiptHash == fingerprint, token))
        {
            var sizes = await db.AgentMemoryReadReceipts.Where(x => x.RuntimeId == invocation.RuntimeId).Select(x => x.EvidenceJson.Length).Take(65).ToArrayAsync(token);
            if (sizes.Length >= 64 || sizes.Sum() + capture.EvidenceJson.Length > 2_097_152)
                throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.ReceiptCapacity);
            db.AgentMemoryReadReceipts.Add(new AgentMemoryReadReceipt { Id = Guid.NewGuid(), OrganizationId = organization,
                EmployeeId = invocation.EmployeeId, InstallationId = installation, RuntimeId = invocation.RuntimeId, WorkId = invocation.WorkId,
                Attempt = invocation.Attempt, GrantRevision = session.Grant.Revision, Capability = capability,
                EvidenceJson = capture.EvidenceJson, AuthorityHash = authority, ReceiptHash = fingerprint, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
        }
        if (await BeginAsync(session, capability, token) != invocation ||
            authority != await AuthorityHashAsync(organization, invocation.EmployeeId, capture.Partitions, token)) throw Denied();
        foreach (var partition in capture.Partitions) await resolver.AuthorizeAsync(session, partition, PlatformMemoryAction.Read, token);
        await RequireSharedReadConsumerAsync(invocation.WorkId, organization, invocation.EmployeeId, capture.EvidenceJson, capture.Partitions, token);
        await transaction.CommitAsync(token);
    }

    public async Task AuthorizeDispatchAsync(AgentSession session, Guid? expectedWork, CancellationToken token)
    {
        try { await AuthorizeDispatchCoreAsync(session, expectedWork, token); }
        catch (MemoryRuntimeResetRequiredException reset)
        {
            await RequestResetAsync(session, reset.ReasonCode, token, reset.ValidationDiagnostic);
            throw new ProviderDispatchDeniedException();
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not ProviderDispatchDeniedException)
        { throw new ProviderDispatchDeniedException(); }
    }

    private Task<bool> RequestResetAsync(AgentSession session, string reason, CancellationToken token, string? diagnostic = null) =>
        Guid.TryParse(session.RuntimeInstanceId, out var runtime) && Guid.TryParse(session.TickId, out var tick) &&
        Guid.TryParse(session.InstallationId, out var installation)
            ? new AgentMemoryRuntimeReset(db).RequestAsync(runtime, tick, installation, session.BusinessId, session.Grant.Revision, reason, token, diagnostic)
            : Task.FromResult(false);

    private async Task AuthorizeDispatchCoreAsync(AgentSession session, Guid? expectedWork, CancellationToken token)
    {
        if (!Guid.TryParse(session.RuntimeInstanceId, out var runtime)) throw Denied();
        // Even runtimes without memory-query grants may have received recalled chat context.
        var currentRuntime = await db.AgentRuntimeInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runtime, token);
        if (currentRuntime is null || currentRuntime.MemoryResetRequestedAt is not null) throw Denied();
        if (currentRuntime.MemoryReadEvidenceVersion != AgentRuntimeInstance.CurrentMemoryReadEvidenceVersion)
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.LegacyEvidence);
        // Retain all reads for this runtime: finishing a work item does not erase its prompt state.
        var receipts = await db.AgentMemoryReadReceipts.AsNoTracking().Where(x => x.RuntimeId == runtime).OrderBy(x => x.Id).Take(65).ToArrayAsync(token);
        if (receipts.Length == 0) return;
        if (expectedWork is null) throw Denied();
        if (receipts.Length > 64 || receipts.Sum(x => x.EvidenceJson.Length) > 2_097_152)
            throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.ReceiptCapacity);
        var validator = new MemoryRecallDispatchEvidence(db); var resolver = new AgentMemoryIdentityResolver(db);
        foreach (var receipt in receipts)
        {
            var queuedRecall = receipt.Capability == MemoryRecallDispatchEvidence.QueuedRecallCapability;
            var capability = queuedRecall ? PlatformChatCapabilities.ChatStream : receipt.Capability;
            var current = await BeginAsync(session, capability, token);
            if (current.WorkId != expectedWork) throw Denied();
            var validation = "receipt.binding";
            try
            {
                if (current.EmployeeId != receipt.EmployeeId || receipt.OrganizationId.ToString("D") != session.BusinessId ||
                    receipt.InstallationId.ToString("D") != session.InstallationId || receipt.GrantRevision != session.Grant.Revision) throw Denied();
                if (queuedRecall)
                {
                    validation = "queued-recall.retained-delivery";
                    var work = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == current.WorkId, token);
                    await validator.AuthorizeRetainedDeliveryAsync(receipt, work, token);
                    if (current != await BeginAsync(session, capability, token)) throw Denied();
                    continue;
                }
                validation = "read.evidence";
                var partitions = await validator.ValidateReadAsync(receipt.EvidenceJson, token, preserveInfrastructureFailure: true);
                validation = "read.shared-consumer";
                await RequireSharedReadConsumerAsync(current.WorkId, receipt.OrganizationId, current.EmployeeId, receipt.EvidenceJson, partitions, token);
                foreach (var partition in partitions)
                {
                    validation = "read.partition-authority";
                    await resolver.AuthorizeAsync(session, partition, PlatformMemoryAction.Read, token);
                    if (partition.UserId is { } user)
                    {
                        validation = "read.relationship-consumer";
                        var work = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == current.WorkId, token);
                        await validator.RequireRelationshipConsumerAsync(work, current.EmployeeId, receipt.WorkId, user, token);
                    }
                }
                validation = "read.authority-changed";
                if (receipt.AuthorityHash != await AuthorityHashAsync(receipt.OrganizationId, receipt.EmployeeId, partitions, token)) throw Denied();
                validation = "read.lease-changed";
                if (current != await BeginAsync(session, receipt.Capability, token)) throw Denied();
            }
            catch (Exception error) when (error is ProviderDispatchDeniedException or UnauthorizedAccessException or
                JsonException or FormatException or NullReferenceException or KeyNotFoundException or ArgumentException)
            {
                var code = error.Data["memory.validation"] as string ?? validation;
                throw new MemoryRuntimeResetRequiredException(MemoryRuntimeResetRequiredException.RetainedEvidence,
                    $"validation={code};receipt={receipt.Id:D};work={current.WorkId:D};error={error.GetType().Name}");
            }
        }
    }

    private async Task RequireSharedReadConsumerAsync(Guid workId, Guid organization, Guid employee, string json,
        IReadOnlyList<MemoryPartition> partitions, CancellationToken token)
    {
        if (!partitions.Any(MemorySharedAudiences.IsCanonical)) return;
        var work = await db.AgentWorkItems.AsNoTracking().SingleAsync(x => x.Id == workId, token);
        if (work.SourceType != "chat-turn") return;
        if (!Guid.TryParse(work.SourceId, out var turn)) throw Denied();
        await new MemoryRecallDispatchEvidence(db).AuthorizeChatReadSharedAudienceAsync(json, turn, organization, employee, token);
    }

    private async Task<string> AuthorityHashAsync(Guid organization, Guid employee, IReadOnlyList<MemoryPartition> partitions, CancellationToken token)
    {
        var users = partitions.Where(x => x.UserId is not null).Select(x => Guid.Parse(x.UserId!)).Append(employee).Distinct().Order().ToArray();
        if (users.Length > 33) throw Denied();
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization && users.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.ApplicationUserId, x.AgentInstallationId, x.IsActive, x.ArchivedAt, x.EmployeeType,
                x.RoleId, x.ReportsToOrganizationUserId, x.PermissionLevel, x.Revision }).ToArrayAsync(token);
        if (people.Length != users.Length || people.Any(x => !x.IsActive || x.ArchivedAt != null)) throw Denied();
        var now = DateTimeOffset.UtcNow;
        var memberships = await db.TeamMemberships.AsNoTracking().Where(x => x.OrganizationId == organization && users.Contains(x.OrganizationUserId) &&
            x.JoinedAt <= now && (x.EndedAt == null || x.EndedAt > now)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.TeamId, x.OrganizationUserId, x.TeamRoleId, x.JoinedAt, x.EndedAt,
                x.Team!.Revision, x.Team.ArchivedAt, x.Team.LeadOrganizationUserId }).Take(257).ToArrayAsync(token);
        if (memberships.Length > 256) throw Denied();
        var roles = people.Select(x => x.RoleId).Concat(memberships.Select(x => x.TeamRoleId)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var definitions = await db.CoreRoles.AsNoTracking().Where(x => roles.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.OrganizationId, x.AuthorityLevel, x.ResponsibilitiesJson, x.UpdatedAt }).ToArrayAsync(token);
        return Hash(JsonSerializer.Serialize(new { people, memberships, definitions }, Json));
    }
}
