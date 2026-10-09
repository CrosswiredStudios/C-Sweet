using AgentCoordinationEvents = CSweet.Agent.SDK.AgentCoordinationEvents;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Memory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    private async Task<string> CaseCoordinationConsumerBindingAsync(AgentWorkItem work, Guid organization,
        Guid employee, Guid itemId, Guid boardId, CancellationToken token)
    {
        // A payload WorkContext, a historical collaboration, or a matching project
        // cannot stand in for the current platform-owned case collaboration turn.
        if (work.Kind != AgentWorkKind.Event || work.Name != AgentCoordinationEvents.TurnRequested ||
            !Guid.TryParseExact(work.CorrelationId, "D", out var sessionId) ||
            !Guid.TryParseExact(work.SourceId, "D", out _)) throw Denied("case.coordination-kind");
        if (db.Database.CurrentTransaction is not null)
        {
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AgentCoordinationSessions\" WHERE \"Id\"={sessionId} FOR SHARE NOWAIT", token);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"MemoryCoordinationConsumerAuthority\" WHERE \"Id\"={sessionId} FOR SHARE NOWAIT", token);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
            { throw new DbUpdateConcurrencyException("The coordination consumer is changing. Refresh before reading memory.", error); }
        }
        var session = await db.AgentCoordinationSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sessionId &&
            x.OrganizationId == organization && x.SourceKind == "WorkItem" && x.SourceWorkItemId == itemId && x.SourceBoardId == boardId &&
            x.CurrentAgentWorkItemId == work.Id && x.CurrentOrganizationUserId == employee &&
            (x.Status == AgentCoordinationStatus.Active || x.Status == AgentCoordinationStatus.Summarizing), token)
            ?? throw Denied("case.coordination-current-turn");
        if (session.InitiatorOrganizationUserId == session.TargetOrganizationUserId ||
            session.InitiatorInstallationId == session.TargetInstallationId ||
            (employee == session.InitiatorOrganizationUserId ? session.InitiatorInstallationId :
             employee == session.TargetOrganizationUserId ? session.TargetInstallationId : Guid.Empty) != work.AgentInstallationId)
            throw Denied("case.coordination-identity");
        if (db.Database.CurrentTransaction is not null)
            await MemoryScopedAudienceAuthorization.LockConversationAsync(db, session.ConversationId, token);
        var conversation = await db.CoreConversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.ConversationId &&
            x.OrganizationId == organization && x.ArchivedAt == null && x.MergedIntoConversationId == null, token)
            ?? throw Denied("case.coordination-conversation");
        var now = DateTimeOffset.UtcNow;
        var recipients = await db.ConversationParticipants.AsNoTracking().Where(x => x.ConversationId == session.ConversationId &&
            x.JoinedAt <= now && (x.LeftAt == null || x.LeftAt > now)).Select(x => x.OrganizationUserId).Distinct().Order().Take(65).ToArrayAsync(token);
        if (recipients.Length is < 2 or > 64 || !recipients.Contains(session.InitiatorOrganizationUserId) ||
            !recipients.Contains(session.TargetOrganizationUserId)) throw Denied("case.coordination-recipients");
        var generation = await db.Database.SqlQuery<long>($"SELECT \"Revision\" AS \"Value\" FROM \"MemoryCoordinationConsumerAuthority\" WHERE \"Id\"={sessionId}")
            .SingleOrDefaultAsync(token);
        if (generation < 1) throw Denied("case.coordination-generation");
        var partition = EmployeeMemoryNamespaces.Case(organization.ToString("D"), itemId.ToString("D"), "csweet").Partition;
        var bindings = new List<string> { $"coordination:{sessionId:D}:{generation}:{conversation.MemoryAudienceRevision}:{work.PayloadHash}" };
        var identities = new List<(string Kind, Guid Id)> { ("conversation", conversation.Id) };
        foreach (var recipient in recipients)
        {
            var person = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recipient &&
                x.OrganizationId == organization && x.IsActive && x.ArchivedAt == null, token) ?? throw Denied("case.coordination-recipient");
            if (person.Id == session.InitiatorOrganizationUserId && person.AgentInstallationId != session.InitiatorInstallationId ||
                person.Id == session.TargetOrganizationUserId && person.AgentInstallationId != session.TargetInstallationId)
                throw Denied("case.coordination-installation");
            var reader = person.EmployeeType == EmployeeType.Agent ? person.Id : employee;
            Guid? human = person.EmployeeType == EmployeeType.Human ? person.Id : null;
            identities.Add(("person", person.Id));
            if (person.EmployeeType == EmployeeType.Agent && person.AgentInstallationId is { } installation)
                identities.Add(("installation", installation));
            await MemoryScopedAudienceAuthorization.RequireAsync(db, organization, reader, human, partition, token,
                lockAuthority: db.Database.CurrentTransaction is not null);
            bindings.Add(await MemoryScopedAudienceAuthorization.AuthorityHashAsync(db, organization, reader, human, [partition], token)
                ?? throw Denied());
        }
        bindings.Add(System.Text.Json.JsonSerializer.Serialize(await MemoryAccessAuthorityEvidence.ReadAsync(db, identities, token)));
        return Hash(System.Text.Json.JsonSerializer.Serialize(bindings));
    }
}
