-- Run with psql -v organization_id=... -v installation_id=... -v generated_project_id=... -v existing_project_id=...
-- Removes only an untouched, empty automatic compute project. Keeps the legacy access record
-- rebound so an older running host cannot recreate the synthetic project before it is restarted.
\set ON_ERROR_STOP on
BEGIN ISOLATION LEVEL SERIALIZABLE;
CREATE TEMP TABLE compute_scope_repair ON COMMIT DROP AS SELECT
    :'organization_id'::uuid AS organization_id, :'installation_id'::uuid AS installation_id,
    :'generated_project_id'::uuid AS generated_project_id, :'existing_project_id'::uuid AS existing_project_id;
DO $repair$
DECLARE
    inputs record;
    dependency record;
    has_dependency boolean;
    audit_id uuid := gen_random_uuid();
    wake_id uuid := gen_random_uuid();
    previous_project jsonb;
    previous_health jsonb;
BEGIN
    SELECT * INTO STRICT inputs FROM compute_scope_repair;
    IF inputs.generated_project_id = inputs.existing_project_id THEN
        RAISE EXCEPTION 'Source and destination projects must differ';
    END IF;
    PERFORM 1 FROM "Workstreams" WHERE "Id" IN (inputs.generated_project_id, inputs.existing_project_id) FOR UPDATE;
    PERFORM 1 FROM "ComputeAgentAccess" WHERE "Id" = inputs.installation_id FOR UPDATE;
    IF NOT EXISTS (SELECT 1 FROM "Workstreams" WHERE "Id" = inputs.generated_project_id) AND
        EXISTS (SELECT 1 FROM "ComputeAgentAccess" WHERE "Id" = inputs.installation_id AND
            "OrganizationId" = inputs.organization_id AND "WorkstreamId" = inputs.existing_project_id) THEN
        RAISE NOTICE 'Generated project already removed and access rebound';
        RETURN;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM "Workstreams" project JOIN "CoreOrganizationUsers" employee
            ON employee."Id" = project."AccountableManagerOrganizationUserId"
        JOIN "ComputeAgentAccess" access ON access."WorkstreamId" = project."Id" AND access."Id" = employee."AgentInstallationId"
        WHERE project."Id" = inputs.generated_project_id AND project."OrganizationId" = inputs.organization_id AND
            employee."OrganizationId" = inputs.organization_id AND employee."AgentInstallationId" = inputs.installation_id AND
            access."OrganizationId" = inputs.organization_id AND project."Name" = 'Application testing' AND
            project."Outcome" = 'Run and test software in isolated, temporary compute environments.' AND
            project."Status" = 'Proposed' AND project."Revision" = 1 AND project."SourceProposalId" IS NULL AND project."ProfileKey" IS NULL
    ) THEN RAISE EXCEPTION 'Source is not the expected untouched automatic compute project'; END IF;
    IF NOT EXISTS (
        SELECT 1 FROM "Workstreams" project JOIN "CoreOrganizationUsers" employee ON employee."OrganizationId" = project."OrganizationId"
        WHERE project."Id" = inputs.existing_project_id AND project."OrganizationId" = inputs.organization_id AND
            project."Status" IN ('Approved', 'Active') AND employee."AgentInstallationId" = inputs.installation_id AND
            employee."IsActive" AND employee."ArchivedAt" IS NULL AND
            (project."AccountableManagerOrganizationUserId" = employee."Id" OR EXISTS (
                SELECT 1 FROM "WorkstreamTeamAssignments" assignment JOIN "TeamMemberships" member ON member."TeamId" = assignment."TeamId"
                JOIN "OrganizationTeams" team ON team."Id" = assignment."TeamId"
                WHERE assignment."OrganizationId" = inputs.organization_id AND assignment."WorkstreamId" = project."Id" AND
                    assignment."EndsAt" IS NULL AND member."OrganizationId" = inputs.organization_id AND
                    member."OrganizationUserId" = employee."Id" AND member."EndedAt" IS NULL AND team."ArchivedAt" IS NULL))
    ) THEN RAISE EXCEPTION 'Destination is not an active authorized project for this employee'; END IF;
    -- Check every persisted direct project reference, including tables without foreign keys.
    FOR dependency IN SELECT table_schema, table_name FROM information_schema.columns
        WHERE table_schema = 'public' AND column_name = 'WorkstreamId' AND
            table_name NOT IN ('ComputeAgentAccess', 'ProjectHealthSignals', 'ProjectHealthStates')
    LOOP
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I.%I WHERE "WorkstreamId" = $1)', dependency.table_schema, dependency.table_name)
            INTO has_dependency USING inputs.generated_project_id;
        IF has_dependency THEN RAISE EXCEPTION 'Source has records in %; reconcile them before removal', dependency.table_name; END IF;
    END LOOP;
    IF EXISTS (SELECT 1 FROM "ProjectHealthSignals" WHERE "WorkstreamId" = inputs.generated_project_id AND
        ("SourceKind" <> 'Workstream' OR "SourceId" <> inputs.generated_project_id OR "OrganizationId" <> inputs.organization_id)) OR
        EXISTS (SELECT 1 FROM "ProjectHealthStates" WHERE "WorkstreamId" = inputs.generated_project_id AND
            ("HasActiveWork" OR "ProducerEmployeeId" <> '00000000-0000-0000-0000-000000000000'::uuid OR "OrganizationId" <> inputs.organization_id))
    THEN RAISE EXCEPTION 'Source has substantive project health activity'; END IF;
    IF EXISTS (SELECT 1 FROM "ComputeAgentAccess" WHERE "WorkstreamId" = inputs.generated_project_id AND "Id" <> inputs.installation_id) OR
        EXISTS (SELECT 1 FROM "ScopedActionGrants" WHERE "ScopeId" = inputs.generated_project_id AND
            ("ScopeKind" <> 'Workstream' OR "SubjectId" <> inputs.installation_id OR "SubjectKind" <> 'AgentInstallation' OR "Action" NOT LIKE 'compute.%'))
    THEN RAISE EXCEPTION 'Source has additional access or non-compute grants'; END IF;
    SELECT to_jsonb(project) INTO previous_project FROM "Workstreams" project WHERE "Id" = inputs.generated_project_id;
    SELECT jsonb_build_object('signals', (SELECT jsonb_agg(to_jsonb(signal)) FROM "ProjectHealthSignals" signal
        WHERE "WorkstreamId" = inputs.generated_project_id), 'state', (SELECT to_jsonb(state) FROM "ProjectHealthStates" state
        WHERE "WorkstreamId" = inputs.generated_project_id)) INTO previous_health;
    UPDATE "ScopedActionGrants" SET "RevokedAt" = now(), "Revision" = "Revision" + 1
        WHERE "ScopeKind" = 'Workstream' AND "ScopeId" = inputs.generated_project_id AND "RevokedAt" IS NULL;
    UPDATE "ComputeAgentAccess" SET "WorkstreamId" = inputs.existing_project_id, "GrantsCreatedAt" = now()
        WHERE "Id" = inputs.installation_id AND "OrganizationId" = inputs.organization_id;
    DELETE FROM "ProjectHealthSignals" WHERE "WorkstreamId" = inputs.generated_project_id;
    DELETE FROM "ProjectHealthStates" WHERE "WorkstreamId" = inputs.generated_project_id;
    DELETE FROM "Workstreams" WHERE "Id" = inputs.generated_project_id AND "OrganizationId" = inputs.organization_id;
    INSERT INTO "ComputeAuditOutbox" ("Id", "CreatedAt", "RequestJson") VALUES (audit_id, now(), jsonb_build_object(
        'EventType', 'compute.project-scope.reconciled.v1', 'Category', 'Infrastructure', 'Outcome', 'Accepted',
        'OrganizationId', inputs.organization_id, 'EntityType', 'AgentInstallation', 'EntityId', inputs.installation_id,
        'Actor', jsonb_build_object('Kind', 'Application', 'InstallationId', inputs.installation_id),
        'EventId', audit_id, 'OccurredAt', now(), 'UseAmbientOrganization', false,
        'MetadataJson', jsonb_build_object('generatedProject', previous_project, 'generatedProjectHealth', previous_health,
            'existingProjectId', inputs.existing_project_id)::text)::text);
    INSERT INTO "AgentPlatformEventOutbox" ("Id", "OrganizationId", "TargetInstallationId", "EventType", "DataJson", "IdempotencyKey",
        "Status", "Attempts", "NextAttemptAt", "OccurredAt") VALUES (wake_id, inputs.organization_id, inputs.installation_id,
        'com.csweet.compute.available.v1', jsonb_build_object('workstreamId', inputs.existing_project_id),
        'compute-project-scope-repair:' || inputs.installation_id::text || ':' || inputs.generated_project_id::text, 'Pending', 0, now(), now());
END
$repair$;
COMMIT;
