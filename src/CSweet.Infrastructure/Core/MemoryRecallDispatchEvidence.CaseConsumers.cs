using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    private async Task<string> CaseConsumerBindingAsync(AgentWorkItem work, Guid organization, Guid employee,
        Guid itemId, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) throw Denied("case.consumer-backend");
        var item = await db.CoreWorkTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId &&
            x.OrganizationId == organization && x.BoardId != null && x.ArchivedAt == null, token) ?? throw Denied();
        if (work.SourceType is not ("WorkStageExecution" or "WorkDeliveryStage"))
        {
            // Historical activity and payload hints cannot replace the current server-owned claim.
            if (work.Kind != AgentWorkKind.Event || !Guid.TryParseExact(work.SourceId, "D", out var eventId) ||
                item.ClaimEventId != eventId || item.AssignedEmployeeId != employee ||
                item.AssignedAgentInstallationId != work.AgentInstallationId) throw Denied("case.consumer-claim");
            return Hash($"claim:{itemId:D}:{eventId:D}:{employee:D}:{work.AgentInstallationId:D}");
        }
        if (!Guid.TryParseExact(work.SourceId, "D", out var stageId)) throw Denied("case.consumer-stage");
        if (work.SourceType == "WorkDeliveryStage")
            return await CaseDeliveryConsumerBindingAsync(work, organization, employee, itemId, stageId, token);
        var sql = """
            SELECT jsonb_build_object('stage',s."Id",'stageVersion',sv."Revision",
                'attempt',a."Id",'attemptVersion',av."Revision",'item',i."Id",'itemVersion',iv."Revision",
                'sprint',e."Id",'sprintVersion',ev."Revision")::text
            FROM "WorkStageExecutions" s
            JOIN "WorkItemExecutions" i ON i."Id"=s."ItemExecutionId"
            JOIN "WorkSprintExecutions" e ON e."Id"=i."SprintExecutionId"
            JOIN "WorkExecutionAttempts" a ON a."StageExecutionId"=s."Id" AND a."AgentWorkItemId"=@work
            JOIN "MemoryCaseConsumerAuthority" sv ON sv."Kind"='Stage' AND sv."Id"=s."Id"
            JOIN "MemoryCaseConsumerAuthority" av ON av."Kind"='Attempt' AND av."Id"=a."Id"
            JOIN "MemoryCaseConsumerAuthority" iv ON iv."Kind"='Item' AND iv."Id"=i."Id"
            JOIN "MemoryCaseConsumerAuthority" ev ON ev."Kind"='Sprint' AND ev."Id"=e."Id"
            WHERE s."Id"=@stage AND s."AgentInstallationId"=@installation
                AND (s."OrganizationUserId" IS NULL OR s."OrganizationUserId"=@employee)
                AND s."PrincipalKind"='AgentInstallation' AND s."StageType" IN ('AgentExecution','MemberExecution')
                AND i."WorkItemId"=@item AND e."OrganizationId"=@organization AND e."BoardId"=@board
                AND i."CurrentStageKey"=s."StageKey" AND i."Traversal"=s."Traversal"
                AND e."Status"='Active' AND i."Status" IN ('Pending','Running')
                AND s."Status" IN ('Dispatching','Running') AND a."Status" IN ('Pending','Running')
                AND NOT EXISTS(SELECT 1 FROM "WorkExecutionAttempts" newer
                    WHERE newer."StageExecutionId"=s."Id" AND newer."Attempt">a."Attempt")
            LIMIT 2
            """;
        if (db.Database.CurrentTransaction is not null) sql += " FOR SHARE OF s,i,e,a,sv,av,iv,ev NOWAIT";
        var openedHere = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        command.Parameters.AddWithValue("work", work.Id); command.Parameters.AddWithValue("stage", stageId);
        command.Parameters.AddWithValue("installation", work.AgentInstallationId); command.Parameters.AddWithValue("employee", employee);
        command.Parameters.AddWithValue("item", itemId); command.Parameters.AddWithValue("organization", organization);
        command.Parameters.AddWithValue("board", item.BoardId!.Value);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw Denied("case.consumer-stage");
            var binding = reader.GetString(0);
            if (await reader.ReadAsync(token)) throw Denied("case.consumer-ambiguous-attempt");
            // Durable authority revisions preserve change-and-restore while routine
            // progress, summaries and timestamp updates keep existing reads usable.
            return Hash(binding);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("The case execution is changing. Refresh before reading memory.", error); }
        finally { if (openedHere) await db.Database.CloseConnectionAsync(); }
    }
}
