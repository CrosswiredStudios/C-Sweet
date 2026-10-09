using System.Text.Json;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    public async Task<string> BindScopedReadConsumerAsync(string json, AgentWorkItem work, Guid employee, CancellationToken token)
    {
        if (json.Length > 262144) throw Denied();
        var receipt = JsonSerializer.Deserialize<ReadReceipt>(json, Json) ?? throw Denied();
        if (receipt.Version != 1 || receipt.Partitions is not { Length: <= 32 }) throw Denied();
        var authority = await ScopedConsumerAuthorityAsync(work, employee, receipt.Partitions, token);
        return JsonSerializer.Serialize(receipt with { ScopedAuthorityHash = authority }, Json);
    }

    public async Task AuthorizeScopedReadConsumerAsync(string json, AgentWorkItem work, Guid employee, CancellationToken token)
    {
        if (json.Length > 262144) throw Denied();
        var receipt = JsonSerializer.Deserialize<ReadReceipt>(json, Json) ?? throw Denied();
        if (receipt.Version != 1 || receipt.Partitions is not { Length: <= 32 }) throw Denied();
        var current = await ScopedConsumerAuthorityAsync(work, employee, receipt.Partitions, token);
        if (current != receipt.ScopedAuthorityHash) throw Denied("scoped.consumer-authority-changed");
    }

    private async Task<string?> ScopedConsumerAuthorityAsync(AgentWorkItem work, Guid employee,
        IReadOnlyList<MemoryPartition> partitions, CancellationToken token)
    {
        var scoped = partitions.Where(x => MemoryScopedAudienceAuthorization.Resolve(x) is not null).Distinct()
            .OrderBy(x => x.StorageKey, StringComparer.Ordinal).ToArray();
        if (scoped.Length == 0) return null;
        if (scoped.Length > 32 || work.MemoryErasedAt is not null || !Guid.TryParseExact(work.OrganizationId, "D", out var organization) ||
            !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == employee && x.OrganizationId == organization &&
                x.AgentInstallationId == work.AgentInstallationId, token)) throw Denied();
        var hashes = new List<string>();
        foreach (var partition in scoped)
        {
            try
            {
                await MemoryScopedAudienceAuthorization.RequireAsync(db, organization, employee, null, partition, token,
                    lockAuthority: db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null);
                var audience = MemoryScopedAudienceAuthorization.Resolve(partition)!;
                if (work.SourceType == "chat-turn" && Guid.TryParseExact(work.SourceId, "D", out var turnId))
                {
                    var turn = await db.ChatTurns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == turnId && x.OrganizationId == organization &&
                        x.TargetAgentOrganizationUserId == employee && x.Conversation != null && x.Conversation.ArchivedAt == null &&
                        x.Conversation.MergedIntoConversationId == null, token) ?? throw Denied();
                    if (audience.Audience == MemoryAudienceType.Conversation && partition.ConversationId != turn.ConversationId.ToString("D"))
                        throw Denied("conversation.consumer-audience");
                    if (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null)
                        await MemoryScopedAudienceAuthorization.LockConversationAsync(db, turn.ConversationId, token);
                    var audienceRevision = await db.CoreConversations.AsNoTracking().Where(x => x.Id == turn.ConversationId)
                        .Select(x => x.MemoryAudienceRevision).SingleAsync(token);
                    hashes.Add($"consumer:{turn.ConversationId:D}:{audienceRevision}");
                    var now = DateTimeOffset.UtcNow;
                    var recipients = await db.ConversationParticipants.AsNoTracking().Where(x => x.ConversationId == turn.ConversationId &&
                        x.JoinedAt <= now && (x.LeftAt == null || x.LeftAt > now)).Select(x => x.OrganizationUserId).Distinct().Order().Take(65).ToArrayAsync(token);
                    if (recipients.Length is < 1 or > 64 || !recipients.Contains(employee)) throw Denied();
                    foreach (var recipient in recipients)
                    {
                        var person = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recipient &&
                            x.OrganizationId == organization && x.IsActive && x.ArchivedAt == null, token) ?? throw Denied();
                        var reader = person.EmployeeType == EmployeeType.Agent ? person.Id : employee;
                        Guid? human = person.EmployeeType == EmployeeType.Human ? person.Id : null;
                        await MemoryScopedAudienceAuthorization.RequireAsync(db, organization, reader, human, partition, token,
                            lockAuthority: db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null);
                        hashes.Add(await MemoryScopedAudienceAuthorization.AuthorityHashAsync(db, organization, reader, human, [partition], token)
                            ?? throw Denied());
                    }
                }
                else if (audience.Audience == MemoryAudienceType.Case)
                {
                    hashes.Add(await CaseConsumerBindingAsync(work, organization, employee, Guid.Parse(audience.AudienceId), token));
                    hashes.Add(await MemoryScopedAudienceAuthorization.AuthorityHashAsync(db, organization, employee, null, [partition], token)
                        ?? throw Denied());
                }
                else throw Denied("scoped.consumer-kind");
            }
            catch (UnauthorizedAccessException) { throw Denied("scoped.consumer-audience"); }
        }
        return Hash(JsonSerializer.Serialize(hashes, Json));
    }
}
