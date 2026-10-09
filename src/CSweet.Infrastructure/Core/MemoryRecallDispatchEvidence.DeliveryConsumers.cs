using CSweet.Domain.Setup;
using CSweet.WorkManagement.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace CSweet.Infrastructure.Core;

public sealed partial class MemoryRecallDispatchEvidence
{
    private async Task<string> CaseDeliveryConsumerBindingAsync(AgentWorkItem work, Guid organization, Guid employee,
        Guid itemId, Guid stageId, CancellationToken token)
    {
        if (work.Kind != AgentWorkKind.Capability || work.Name != WorkManagementCapabilityNames.ExecutionRunV2)
            throw Denied("case.consumer-delivery-kind");
        // Aggregate reviews have no sprint identity. Bind the canonical item and current
        // approved scope, candidate, reviewer and exact attempt from server-owned rows.
        // Assignment payloads and release-level scopes cannot supply a case identity.
        var sql = """
            SELECT jsonb_build_object('stage',s."Id",'stageVersion',sv."Revision",
                'attempt',a."Id",'attemptVersion',av."Revision",
                'delivery',e."Id",'deliveryVersion',ev."Revision",'plan',p."Id",'planVersion',pv."Revision",
                'participantVersion',pp.xmin::text)::text
            FROM "WorkStageExecutions" s
            JOIN "WorkDeliveryExecutions" e ON e."Id"=s."DeliveryExecutionId"
            JOIN "WorkDeliveryPlans" p ON p."Id"=e."PlanId"
            JOIN "CoreWorkTasks" t ON t."Id"=e."WorkItemId"
            JOIN "WorkBoards" b ON b."Id"=e."BoardId"
            JOIN "ProjectParticipants" pp ON pp."OrganizationId"=p."OrganizationId"
                AND pp."WorkstreamId"=p."WorkstreamId" AND pp."OrganizationUserId"=@employee
            JOIN "WorkExecutionAttempts" a ON a."StageExecutionId"=s."Id" AND a."AgentWorkItemId"=@work
            JOIN "MemoryCaseConsumerAuthority" sv ON sv."Kind"='Stage' AND sv."Id"=s."Id"
            JOIN "MemoryCaseConsumerAuthority" av ON av."Kind"='Attempt' AND av."Id"=a."Id"
            JOIN "MemoryCaseConsumerAuthority" ev ON ev."Kind"='Delivery' AND ev."Id"=e."Id"
            JOIN "MemoryCaseConsumerAuthority" pv ON pv."Kind"='Plan' AND pv."Id"=p."Id"
            WHERE s."Id"=@stage AND s."ItemExecutionId" IS NULL AND s."AgentInstallationId"=@installation
                AND s."OrganizationUserId"=@employee AND s."PrincipalKind"='AgentInstallation' AND s."StageType"='AgentExecution'
                AND t."Id"=@item AND t."OrganizationId"=@organization AND t."ArchivedAt" IS NULL
                AND t."BoardId"=b."Id" AND b."OrganizationId"=@organization AND b."ArchivedAt" IS NULL
                AND b."WorkstreamId"=p."WorkstreamId" AND pp."RemovedAt" IS NULL AND pp."JoinedAt"<=CURRENT_TIMESTAMP
                AND p."OrganizationId"=@organization AND p."Status"='Active' AND e."ScopeRevision"=p."ScopeRevision"
                AND e."Scope" IN ('Story','Epic') AND e."Status" IN ('Ready','Running') AND e."CurrentStageKey"=s."StageKey"
                AND s."Status" IN ('Dispatching','Running') AND a."Status" IN ('Pending','Running')
                AND s."StageKey" IN ('quality','technical-review','build-readiness','manager-review')
                AND jsonb_typeof(e."CandidateJson")='object' AND e."CandidateJson"->'scopeRevision'=to_jsonb(p."ScopeRevision")
                AND length(e."CandidateJson"->>'digest')=64
                AND octet_length(p."ScopesJson"::text)<=2097152
                AND jsonb_array_length(CASE WHEN jsonb_typeof(p."ScopesJson")='array' THEN p."ScopesJson" ELSE '[]'::jsonb END) BETWEEN 1 AND 128
                AND (SELECT count(*) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p."ScopesJson")='array' THEN p."ScopesJson" ELSE '[]'::jsonb END) scope
                    WHERE scope->>'scope'=e."Scope" AND scope->'itemId'=to_jsonb(t."Id")
                        AND scope->'boardId'=to_jsonb(b."Id") AND scope->'planningRevision'=to_jsonb(t."PlanningRevision")
                        AND ((s."StageKey"='manager-review' AND e."Scope"='Epic' AND b."ManagerOrganizationUserId"=@employee)
                            OR (s."StageKey"<>'manager-review' AND
                                jsonb_array_length(CASE WHEN jsonb_typeof(scope->'stages')='array' THEN scope->'stages' ELSE '[]'::jsonb END) BETWEEN 1 AND 16 AND
                                (SELECT count(*) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(scope->'stages')='array' THEN scope->'stages' ELSE '[]'::jsonb END) assignment
                                    WHERE assignment->>'stageKey'=s."StageKey" AND assignment->>'principalKind'='AgentInstallation'
                                        AND assignment->'organizationUserId'=to_jsonb(@employee::uuid)
                                        AND assignment->'agentInstallationId'=to_jsonb(@installation::uuid))=1)))=1
                AND NOT EXISTS(SELECT 1 FROM "WorkExecutionAttempts" newer
                    WHERE newer."StageExecutionId"=s."Id" AND newer."Attempt">a."Attempt")
                AND NOT EXISTS(SELECT 1 FROM "WorkStageExecutions" newer
                    WHERE newer."DeliveryExecutionId"=e."Id" AND newer."StageKey"=s."StageKey"
                        AND newer."Id"<>s."Id" AND newer."Traversal">=s."Traversal")
            LIMIT 2
            """;
        if (db.Database.CurrentTransaction is not null) sql += " FOR SHARE OF s,e,p,t,b,pp,a,sv,av,ev,pv NOWAIT";
        var openedHere = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection(),
            db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
        command.Parameters.AddWithValue("work", work.Id); command.Parameters.AddWithValue("stage", stageId);
        command.Parameters.AddWithValue("installation", work.AgentInstallationId); command.Parameters.AddWithValue("employee", employee);
        command.Parameters.AddWithValue("item", itemId); command.Parameters.AddWithValue("organization", organization);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw Denied("case.consumer-delivery");
            var binding = reader.GetString(0);
            if (await reader.ReadAsync(token)) throw Denied("case.consumer-ambiguous-attempt");
            return Hash(binding);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.LockNotAvailable)
        { throw new DbUpdateConcurrencyException("The case delivery is changing. Refresh before reading memory.", error); }
        finally { if (openedHere) await db.Database.CloseConnectionAsync(); }
    }
}
