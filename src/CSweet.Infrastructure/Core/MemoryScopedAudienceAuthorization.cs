using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Core;
using CSweet.Domain.Security;
using CSweet.Domain.WorkManagement;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Security;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

/// <summary>Server-owned conversation and canonical work-item memory audiences.</summary>
public static class MemoryScopedAudienceAuthorization
{
    public static MemoryNamespace? Resolve(MemoryPartition partition)
    {
        if (partition.ApplicationId != "csweet" || partition.AgentId is not null || partition.UserId is not null)
            return null;
        if (CanonicalId(partition.ConversationId) is { } conversation && partition.CustomNamespace is null)
            return new(partition, MemoryScope.Conversation, MemoryAudienceType.Conversation, conversation.ToString("D"));
        if (partition.ConversationId is null && partition.CustomNamespace?.StartsWith("case:", StringComparison.Ordinal) == true &&
            CanonicalId(partition.CustomNamespace[5..]) is { } item)
            return EmployeeMemoryNamespaces.Case(partition.TenantId, item.ToString("D"), "csweet");
        return null;
    }

    private static Guid? CanonicalId(string? value) => Guid.TryParseExact(value, "D", out var id) &&
        id != Guid.Empty && value == id.ToString("D") ? id : null;

    public static async Task RequireAsync(CSweetDbContext db, Guid organization, Guid employee, Guid? human,
        MemoryPartition partition, CancellationToken token, bool lockAuthority = false, bool retained = false)
    {
        var audience = Resolve(partition) ?? throw new UnauthorizedAccessException();
        if (partition.TenantId != organization.ToString("D") || human == employee) throw new UnauthorizedAccessException();
        var people = human.HasValue ? new[] { employee, human.Value } : [employee];
        if (lockAuthority)
            foreach (var person in people.Order())
                await LockAsync(db, $"SELECT 1 FROM \"CoreOrganizationUsers\" WHERE \"Id\"={person} FOR SHARE NOWAIT", token);
        var agent = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == employee &&
            x.OrganizationId == organization && x.EmployeeType == EmployeeType.Agent && x.IsActive && x.ArchivedAt == null &&
            x.AgentInstallation != null && x.AgentInstallation.IsEnabled &&
            x.AgentInstallation.BusinessId == organization.ToString("D"), token) ?? throw new UnauthorizedAccessException();
        if (await db.CoreOrganizationUsers.AsNoTracking().CountAsync(x => x.OrganizationId == organization &&
            x.AgentInstallationId == agent.AgentInstallationId && x.EmployeeType == EmployeeType.Agent && x.IsActive &&
            x.ArchivedAt == null, token) != 1) throw new UnauthorizedAccessException();
        if (human is { } actor && !await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x => x.Id == actor &&
            x.OrganizationId == organization && x.EmployeeType == EmployeeType.Human && x.IsActive && x.ArchivedAt == null, token))
            throw new UnauthorizedAccessException();
        var id = Guid.Parse(audience.AudienceId);
        if (audience.Audience == MemoryAudienceType.Conversation)
        {
            if (lockAuthority)
                await LockConversationAsync(db, id, token);
            var now = DateTimeOffset.UtcNow;
            if (!await db.CoreConversations.AsNoTracking().AnyAsync(x => x.Id == id && x.OrganizationId == organization &&
                (retained || x.ArchivedAt == null && x.MergedIntoConversationId == null), token) ||
                await db.ConversationParticipants.AsNoTracking().Where(x => x.ConversationId == id &&
                    people.Contains(x.OrganizationUserId) && x.JoinedAt <= now && (x.LeftAt == null || x.LeftAt > now))
                    .Select(x => x.OrganizationUserId).Distinct().CountAsync(token) != people.Length)
                throw new UnauthorizedAccessException();
            return;
        }
        if (lockAuthority)
            await LockAsync(db, $"SELECT 1 FROM \"CoreWorkTasks\" WHERE \"Id\"={id} FOR SHARE NOWAIT", token);
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id &&
            x.OrganizationId == organization && (retained || x.ArchivedAt == null), token) ?? throw new UnauthorizedAccessException();
        if (item.BoardId is not { } boardId) throw new UnauthorizedAccessException();
        if (lockAuthority)
            await LockAsync(db, $"SELECT 1 FROM \"WorkBoards\" WHERE \"Id\"={boardId} FOR SHARE NOWAIT", token);
        var board = await db.WorkBoards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == boardId &&
            x.OrganizationId == organization && (retained || x.ArchivedAt == null), token) ?? throw new UnauthorizedAccessException();
        foreach (var person in people)
        {
            var kind = person == employee ? GrantSubjectKind.AgentInstallation : GrantSubjectKind.OrganizationUser;
            var subject = person == employee ? agent.AgentInstallationId!.Value : person;
            if (board.Kind == WorkBoardKind.Personal && board.OwnerOrganizationUserId != person)
                throw new UnauthorizedAccessException();
            if (lockAuthority)
                await LockAsync(db, $"SELECT 1 FROM \"ScopedActionGrants\" WHERE \"OrganizationId\"={organization} AND \"SubjectId\"={subject} ORDER BY \"Id\" FOR SHARE NOWAIT", token);
            var authorization = new ScopedActionAuthorizationService(db);
            foreach (var action in new[] { board.Kind == WorkBoardKind.Personal ? PersonalTodoActions.Read : WorkItemActions.Read,
                WorkItemActions.ReadComments })
            {
                var allowed = (await authorization.AuthorizeAsync(organization, kind, subject, action, GrantScopeKind.WorkItem, id, token)).Allowed ||
                    (await authorization.AuthorizeAsync(organization, kind, subject, action, GrantScopeKind.Board, boardId, token)).Allowed ||
                    board.TeamId is { } team && (await authorization.AuthorizeAsync(organization, kind, subject,
                        action, GrantScopeKind.Team, team, token)).Allowed;
                if (!allowed) throw new UnauthorizedAccessException();
            }
        }
    }

    internal static async Task LockConversationAsync(CSweetDbContext db, Guid conversation, CancellationToken token)
    {
        await LockAsync(db, $"SELECT 1 FROM \"CoreConversations\" WHERE \"Id\"={conversation} FOR SHARE NOWAIT", token);
        await LockAsync(db, $"SELECT 1 FROM \"ConversationParticipants\" WHERE \"ConversationId\"={conversation} ORDER BY \"Id\" FOR SHARE NOWAIT", token);
    }

    private static async Task LockAsync(CSweetDbContext db, FormattableString sql, CancellationToken token)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Scoped memory review requires a PostgreSQL transaction.");
        try { await db.Database.ExecuteSqlInterpolatedAsync(sql, token); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("The memory audience is changing. Refresh before reviewing.", error); }
    }

    public static async Task<MemoryPartition[]> ReadableAsync(CSweetDbContext db, Guid organization,
        Guid employee, Guid human, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var conversations = await db.CoreConversations.AsNoTracking().Where(x => x.OrganizationId == organization &&
            x.ArchivedAt == null && x.MergedIntoConversationId == null &&
            db.ConversationParticipants.Any(p => p.ConversationId == x.Id && p.OrganizationUserId == employee &&
                p.JoinedAt <= now && (p.LeftAt == null || p.LeftAt > now)) &&
            db.ConversationParticipants.Any(p => p.ConversationId == x.Id && p.OrganizationUserId == human &&
                p.JoinedAt <= now && (p.LeftAt == null || p.LeftAt > now))).OrderBy(x => x.Id).Select(x => x.Id).Take(129).ToArrayAsync(token);
        var installation = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.Id == employee && x.OrganizationId == organization)
            .Select(x => x.AgentInstallationId).SingleAsync(token) ?? throw new UnauthorizedAccessException();
        var items = await db.CoreWorkTasks.AsNoTracking().Where(x => x.OrganizationId == organization && x.ArchivedAt == null &&
            x.Board != null && x.Board.OrganizationId == organization && x.Board.ArchivedAt == null &&
            db.ScopedActionGrants.Any(g => g.OrganizationId == organization && g.RevokedAt == null &&
                (g.ExpiresAt == null || g.ExpiresAt > now) && g.SubjectKind == GrantSubjectKind.AgentInstallation &&
                g.SubjectId == installation && (g.Action == WorkItemActions.Read || g.Action == PersonalTodoActions.Read) &&
                (g.ScopeKind == GrantScopeKind.Organization || g.ScopeKind == GrantScopeKind.WorkItem && g.ScopeId == x.Id ||
                    g.ScopeKind == GrantScopeKind.Board && g.ScopeId == x.BoardId ||
                    g.ScopeKind == GrantScopeKind.Team && x.Board.TeamId != null && g.ScopeId == x.Board.TeamId)))
            .OrderBy(x => x.Id).Select(x => x.Id).Take(129).ToArrayAsync(token);
        if (conversations.Length > 128 || items.Length > 128) throw new InvalidOperationException("memory_ingestion_audience_capacity");
        var tenant = organization.ToString("D");
        var candidates = conversations.Select(x => new MemoryPartition(tenant, "csweet", ConversationId: x.ToString("D")))
            .Concat(items.Select(x => EmployeeMemoryNamespaces.Case(tenant, x.ToString("D"), "csweet").Partition));
        var result = new List<MemoryPartition>();
        foreach (var candidate in candidates)
        {
            try { await RequireAsync(db, organization, employee, human, candidate, token); result.Add(candidate); }
            catch (UnauthorizedAccessException) { }
        }
        return result.ToArray();
    }

    public static async Task<string?> AuthorityHashAsync(CSweetDbContext db, Guid organization,
        Guid employee, Guid? human, IEnumerable<MemoryPartition> partitions, CancellationToken token, bool retained = false)
    {
        var scoped = partitions.Where(x => Resolve(x) is not null).Distinct().OrderBy(x => x.StorageKey, StringComparer.Ordinal).ToArray();
        if (scoped.Length == 0) return null;
        if (scoped.Length > 32 || !db.Database.IsNpgsql()) throw new UnauthorizedAccessException();
        foreach (var partition in scoped) await RequireAsync(db, organization, employee, human, partition, token, retained: retained);
        var conversations = scoped.Where(x => x.ConversationId is not null).Select(x => Guid.Parse(x.ConversationId!)).ToArray();
        var items = scoped.Where(x => x.ConversationId is null).Select(x => Guid.Parse(x.CustomNamespace![5..])).ToArray();
        var subjects = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organization &&
            (x.Id == employee || x.Id == human)).Select(x => x.AgentInstallationId ?? x.Id).ToArrayAsync(token);
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT kind,value FROM (
                SELECT 'conversation' AS kind,jsonb_build_object('id',t."Id",'revision',t."MemoryAudienceRevision")::text AS value
                    FROM "CoreConversations" t WHERE t."Id"=ANY(@conversations) AND t."OrganizationId"=@organization
                UNION ALL SELECT 'item',jsonb_build_object('id',t."Id",'revision',t."MemoryAudienceRevision")::text FROM "CoreWorkTasks" t
                    WHERE t."Id"=ANY(@items) AND t."OrganizationId"=@organization
                UNION ALL SELECT 'board',jsonb_build_object('id',t."Id",'revision',t."MemoryAudienceRevision")::text FROM "WorkBoards" t
                    WHERE t."Id" IN (SELECT "BoardId" FROM "CoreWorkTasks" WHERE "Id"=ANY(@items) AND "OrganizationId"=@organization)
                UNION ALL SELECT 'grant',to_jsonb(t)::text || ':' || t.xmin::text FROM "ScopedActionGrants" t
                    WHERE t."OrganizationId"=@organization AND t."SubjectId"=ANY(@subjects) AND cardinality(@items)>0
                    AND t."Action"=ANY(@actions)
                    AND (t."ScopeKind"='Organization' OR t."ScopeId"=ANY(@items) OR t."ScopeId" IN
                        (SELECT "BoardId" FROM "CoreWorkTasks" WHERE "Id"=ANY(@items)) OR t."ScopeId" IN
                        (SELECT b."TeamId" FROM "WorkBoards" b JOIN "CoreWorkTasks" w ON w."BoardId"=b."Id" WHERE w."Id"=ANY(@items)))
            ) authority ORDER BY 1,2 LIMIT 513
            """, (NpgsqlConnection)db.Database.GetDbConnection(), db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("conversations", conversations); command.Parameters.AddWithValue("items", items);
        command.Parameters.AddWithValue("subjects", subjects);
        command.Parameters.AddWithValue("actions", new[] { WorkItemActions.Read, WorkItemActions.ReadComments, PersonalTodoActions.Read });
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { scoped, retained }));
        await using var reader = await command.ExecuteReaderAsync(token); var rows = 0; long bytes = 0;
        while (await reader.ReadAsync(token))
        {
            var value = Encoding.UTF8.GetBytes(reader.GetString(0) + ":" + reader.GetString(1));
            if (++rows > 512 || (bytes += value.Length) > 2_097_152) throw new UnauthorizedAccessException();
            digest.AppendData(BitConverter.GetBytes(value.Length)); digest.AppendData(value);
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }
}
