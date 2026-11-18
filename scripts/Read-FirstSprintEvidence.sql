-- psql -X -v organization_id=<organization-guid> -f scripts/Read-FirstSprintEvidence.sql
-- One row per first sprint, including personal-board exclusion. An empty result means no project sprint exists.
\set ON_ERROR_STOP on
BEGIN READ ONLY;
WITH sprints AS (
    SELECT s.*, b."WorkstreamId" AS project_id
    FROM "WorkSprints" s JOIN "WorkBoards" b ON b."Id" = s."BoardId" AND b."OrganizationId" = s."OrganizationId"
    WHERE s."OrganizationId" = :'organization_id'::uuid AND s."Sequence" = 1 AND b."WorkstreamId" IS NOT NULL
), claims AS (
    SELECT s."Id" AS sprint_id, e."Id" AS execution_id, u."EmployeeType" AS starter_type,
           t."Id" AS task_id, t."ParentWorkTaskId" AS story_id, t."ProposalCoordinationSessionId" AS contribution_id,
           t."ProposalArtifactDigest" AS contribution_digest, g."Id" AS stage_id, g."StageKey" AS stage,
           g."OrganizationUserId" AS employee_id, w."AgentInstallationId" AS installation_id,
           p."Id" AS claim_id, p."ClaimedAt" AS claimed_at
    FROM sprints s
    JOIN "WorkSprintExecutions" e ON e."SprintId" = s."Id" AND e."BoardId" = s."BoardId" AND e."OrganizationId" = s."OrganizationId"
    JOIN "CoreOrganizationUsers" u ON u."Id" = e."StartedByOrganizationUserId" AND u."OrganizationId" = s."OrganizationId"
    JOIN "WorkItemExecutions" i ON i."SprintExecutionId" = e."Id"
    JOIN "CoreWorkTasks" t ON t."Id" = i."WorkItemId" AND t."IsExecutable" AND t."BoardId" = s."BoardId"
    JOIN "WorkStageExecutions" g ON g."ItemExecutionId" = i."Id" AND g."StageKey" IN ('development','specialist-execution')
    JOIN "WorkExecutionAttempts" a ON a."StageExecutionId" = g."Id" AND a."StartedAt" IS NOT NULL
    JOIN "AgentWorkItems" w ON w."Id" = a."AgentWorkItemId" AND w."SourceType" = 'WorkStageExecution'
        AND w."SourceId" = g."Id"::text AND w."AgentInstallationId" = g."AgentInstallationId"
    JOIN "AgentWorkAttempts" p ON p."AgentWorkItemId" = w."Id" AND p."ClaimedAt" >= e."StartedAt"
    JOIN "AgentRuntimeInstances" r ON r."Id" = p."RuntimeInstanceId" AND r."AgentInstallationId" = g."AgentInstallationId"
    JOIN "CoreOrganizationUsers" worker ON worker."Id" = g."OrganizationUserId" AND worker."OrganizationId" = s."OrganizationId"
)
SELECT s."OrganizationId", s.project_id, s."BoardId", s."Id" AS sprint_id, s."Status", s."CapacityPoints", s."StartedAt",
       (SELECT count(*) FROM "CoreWorkTasks" t WHERE t."SprintId" = s."Id" AND t."IsExecutable") AS committed_leaves,
       c.execution_id, c.starter_type, c.story_id, c.task_id, c.contribution_id, c.contribution_digest,
       c.stage_id, c.stage, c.employee_id, c.installation_id, c.claim_id, c.claimed_at,
       (SELECT count(*) FROM "AuditEvents" audit WHERE audit."OrganizationId" = s."OrganizationId"
          AND audit."OccurredAt" <= COALESCE(c.claimed_at, now()) AND audit."ActorKind" = 'Human'
          AND (audit."EventType" ILIKE '%retry%' OR audit."EventType" ILIKE '%requeue%')) AS audited_human_retries,
       (SELECT count(*) FROM "WorkOrchestrationEvents" event
          JOIN "CoreOrganizationUsers" actor ON actor."OrganizationId" = s."OrganizationId"
            AND lower(event."DataJson"::jsonb->>'id') = actor."Id"::text
          WHERE event."BoardId" = s."BoardId" AND event."EventType" = 'stage.retry.requested'
            AND event."OccurredAt" <= COALESCE(c.claimed_at, now()) AND actor."EmployeeType" = 'Human') AS human_stage_retries
FROM sprints s LEFT JOIN LATERAL (
    SELECT * FROM claims WHERE sprint_id = s."Id" ORDER BY claimed_at, claim_id LIMIT 1
) c ON true
ORDER BY s."CreatedAt", s."Id";
COMMIT;
