using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemoryModelDiagnosticErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClearedModelRuns",
                table: "MemoryErasureReceipts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MemoryErasedAt",
                table: "AgentRunLogs",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.AddColumn<string>(name: "MemoryErasureAuditJson", table: "AgentRunLogs",
                type: "character varying(262144)", maxLength: 262144, nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_AgentWorkItemId",
                table: "AgentRunLogs",
                column: "AgentWorkItemId");
            migrationBuilder.Sql(InstallGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RemoveGuards);
            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_AgentWorkItemId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "ClearedModelRuns",
                table: "MemoryErasureReceipts");

            migrationBuilder.DropColumn(
                name: "MemoryErasedAt",
                table: "AgentRunLogs");
            migrationBuilder.DropColumn(name: "MemoryErasureAuditJson", table: "AgentRunLogs");
        }

        internal const string RemoveGuards = """
            DO $$ BEGIN
                IF EXISTS(SELECT 1 FROM "AgentRunLogs" WHERE "MemoryErasedAt" IS NOT NULL) OR
                   EXISTS(SELECT 1 FROM "MemoryErasureReceipts" WHERE "ClearedModelRuns">0) THEN
                    RAISE EXCEPTION 'memory_model_erasure_downgrade_requires_snapshot';
                END IF;
            END $$;
            DROP TRIGGER csweet_erased_model_diagnostics ON "AgentRunLogs";
            DROP TRIGGER csweet_erased_model_truncate ON "AgentRunLogs";
            DROP TRIGGER csweet_erased_model_outbox ON "ComputeAuditOutbox";
            DROP TRIGGER csweet_erased_model_audit ON "AuditEvents";
            DROP TRIGGER csweet_erased_model_payload ON "AuditEventPayloads";
            DROP FUNCTION csweet_erased_model_diagnostics();
            DROP FUNCTION csweet_erased_model_truncate();
            DROP FUNCTION csweet_erased_model_audit();
            """;

        internal const string InstallGuards = """
            CREATE FUNCTION csweet_erased_model_diagnostics() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE erased timestamptz; bound boolean; scrub boolean:=false;
            BEGIN
                IF TG_OP='DELETE' THEN
                    IF OLD."MemoryErasedAt" IS NOT NULL THEN RAISE EXCEPTION 'memory_erased_model_immutable'; END IF;
                    RETURN OLD;
                END IF;
                IF TG_OP='INSERT' AND (NEW."MemoryErasedAt" IS NOT NULL OR NEW."MemoryErasureAuditJson" IS NOT NULL) THEN
                    RAISE EXCEPTION 'memory_erased_model_immutable';
                END IF;
                IF TG_OP='UPDATE' THEN
                    IF NEW."Id" IS DISTINCT FROM OLD."Id" OR OLD."MemoryErasedAt" IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
                        RAISE EXCEPTION 'memory_erased_model_immutable';
                    END IF;
                    IF OLD."MemoryErasedAt" IS NULL AND NEW."MemoryErasedAt" IS NOT NULL THEN
                        scrub:=NEW."OrganizationId" IS NOT DISTINCT FROM OLD."OrganizationId" AND
                            NEW."EmployeeId" IS NOT DISTINCT FROM OLD."EmployeeId" AND
                            NEW."AgentInstallationId" IS NOT DISTINCT FROM OLD."AgentInstallationId" AND
                            NEW."AgentWorkItemId" IS NOT DISTINCT FROM OLD."AgentWorkItemId" AND
                            NEW."AgentWorkAttemptId" IS NOT DISTINCT FROM OLD."AgentWorkAttemptId" AND
                            NEW."ChatTurnId" IS NOT DISTINCT FROM OLD."ChatTurnId" AND
                            NEW."PromptPreview" IS NULL AND NEW."OutputPreview" IS NULL AND NEW."FailureMessage" IS NULL AND
                            NEW."InferenceSettingsJson" IS NULL AND NEW."UsageAdditionalCountsJson" IS NULL AND
                            (to_jsonb(NEW)-ARRAY['MemoryErasedAt','MemoryErasureAuditJson','PromptPreview','OutputPreview',
                              'FailureMessage','InferenceSettingsJson','UsageAdditionalCountsJson']) IS NOT DISTINCT FROM
                            (to_jsonb(OLD)-ARRAY['MemoryErasedAt','MemoryErasureAuditJson','PromptPreview','OutputPreview',
                              'FailureMessage','InferenceSettingsJson','UsageAdditionalCountsJson']);
                        scrub:=scrub AND NEW."MemoryErasureAuditJson" IS NOT NULL AND jsonb_typeof(NEW."MemoryErasureAuditJson"::jsonb)='object';
                        IF NOT scrub THEN RAISE EXCEPTION 'memory_erased_model_immutable'; END IF;
                    ELSIF NEW."MemoryErasureAuditJson" IS DISTINCT FROM OLD."MemoryErasureAuditJson" THEN
                        RAISE EXCEPTION 'memory_erased_model_immutable';
                    END IF;
                    IF OLD."AgentWorkItemId" IS NOT NULL AND OLD."AgentWorkItemId" IS DISTINCT FROM NEW."AgentWorkItemId" AND
                        (NOT EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "Id"=OLD."AgentWorkItemId") OR
                         EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "Id"=OLD."AgentWorkItemId" AND "MemoryErasedAt" IS NOT NULL)) THEN
                        RAISE EXCEPTION 'memory_erased_model_immutable';
                    END IF;
                END IF;
                IF NEW."AgentWorkItemId" IS NOT NULL THEN
                    SELECT true,"MemoryErasedAt" INTO bound,erased FROM "AgentWorkItems" WHERE "Id"=NEW."AgentWorkItemId" FOR SHARE;
                    IF (bound IS NOT TRUE OR erased IS NOT NULL) AND NOT scrub THEN RAISE EXCEPTION 'memory_erased_model_immutable'; END IF;
                END IF;
                IF NEW."ChatTurnId" IS NOT NULL AND NOT scrub THEN
                    SELECT "MemoryErasedAt" INTO erased FROM "ChatTurns" WHERE "Id"=NEW."ChatTurnId" FOR SHARE;
                    IF erased IS NOT NULL AND NEW."MemoryErasedAt" IS NULL THEN RAISE EXCEPTION 'memory_erased_model_immutable'; END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_erased_model_diagnostics BEFORE INSERT OR UPDATE OR DELETE ON "AgentRunLogs"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_model_diagnostics();
            CREATE FUNCTION csweet_erased_model_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS(SELECT 1 FROM "AgentRunLogs" WHERE "MemoryErasedAt" IS NOT NULL) THEN
                    RAISE EXCEPTION 'memory_model_erasure_downgrade_requires_snapshot';
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER csweet_erased_model_truncate BEFORE TRUNCATE ON "AgentRunLogs"
                FOR EACH STATEMENT EXECUTE FUNCTION csweet_erased_model_truncate();
            CREATE FUNCTION csweet_erased_model_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE model uuid; erased timestamptz; permitted jsonb; event uuid; prior_model uuid;
            BEGIN
                IF TG_OP='UPDATE' THEN
                    IF TG_TABLE_NAME='ComputeAuditOutbox' THEN prior_model:=OLD."SourceEntityId";
                    ELSIF TG_TABLE_NAME='AuditEvents' THEN prior_model:=OLD."EntityId";
                    ELSE SELECT "EntityId" INTO prior_model FROM "AuditEvents" WHERE "Id"=OLD."AuditEventId"; END IF;
                    IF prior_model IS NOT NULL AND EXISTS(SELECT 1 FROM "AgentRunLogs" WHERE "Id"=prior_model AND "MemoryErasedAt" IS NOT NULL) THEN
                        IF TG_TABLE_NAME<>'ComputeAuditOutbox' AND NEW IS DISTINCT FROM OLD THEN RAISE EXCEPTION 'memory_erased_model_immutable'; END IF;
                        IF TG_TABLE_NAME='ComputeAuditOutbox' AND (NEW."SourceEntityId" IS DISTINCT FROM OLD."SourceEntityId" OR
                            NEW."SourceEntityType" IS DISTINCT FROM OLD."SourceEntityType" OR NEW."RequestJson" IS DISTINCT FROM OLD."RequestJson" OR
                            NEW."ProtectedRequest" IS DISTINCT FROM OLD."ProtectedRequest" OR NEW."Id" IS DISTINCT FROM OLD."Id") THEN
                            RAISE EXCEPTION 'memory_erased_model_immutable';
                        END IF;
                    END IF;
                END IF;
                IF TG_TABLE_NAME='ComputeAuditOutbox' THEN
                    model:=NEW."SourceEntityId"; event:=NEW."Id";
                    IF TG_OP='UPDATE' AND NEW."SourceEntityId" IS NOT DISTINCT FROM OLD."SourceEntityId" AND
                        NEW."SourceEntityType" IS NOT DISTINCT FROM OLD."SourceEntityType" AND NEW."RequestJson" IS NOT DISTINCT FROM OLD."RequestJson" AND
                        NEW."ProtectedRequest" IS NOT DISTINCT FROM OLD."ProtectedRequest" AND NEW."Id" IS NOT DISTINCT FROM OLD."Id" THEN RETURN NEW; END IF;
                ELSIF TG_TABLE_NAME='AuditEvents' THEN model:=NEW."EntityId"; event:=NEW."Id";
                ELSE SELECT "EntityId" INTO model FROM "AuditEvents" WHERE "Id"=NEW."AuditEventId"; event:=NEW."AuditEventId"; END IF;
                IF model IS NOT NULL THEN
                    SELECT "MemoryErasedAt","MemoryErasureAuditJson"::jsonb->event::text INTO erased,permitted
                        FROM "AgentRunLogs" WHERE "Id"=model FOR SHARE;
                    IF erased IS NOT NULL THEN
                        IF TG_TABLE_NAME='AuditEvents' THEN
                            IF permitted IS NULL OR NEW."EventType" IS DISTINCT FROM permitted->>'eventType' OR
                                upper(NEW."PayloadSha256") IS DISTINCT FROM permitted->>'payloadHash' OR NEW."EntityType" IS DISTINCT FROM 'AgentRunLog' OR
                                upper(NEW."EvidenceSha256") IS DISTINCT FROM permitted->>'evidenceHash' OR
                                upper(encode(sha256(convert_to(coalesce(NEW."PayloadPreview",''),'UTF8')),'hex')) IS DISTINCT FROM permitted->>'previewHash' OR
                                (NEW."Summary" IS DISTINCT FROM NEW."EventType" AND NOT (NEW."EventType"='model.response.chunk' AND NEW."Summary" IS NULL)) OR
                                NEW."MetadataJson" IS NOT NULL OR NEW."ErrorCode" IS NOT NULL OR NEW."ErrorMessage" IS NOT NULL THEN
                                RAISE EXCEPTION 'memory_erased_model_immutable';
                            END IF;
                        ELSIF TG_TABLE_NAME='AuditEventPayloads' THEN
                            IF permitted IS NULL OR NOT EXISTS(SELECT 1 FROM "AuditEvents" WHERE "Id"=event AND
                                "EventType"=permitted->>'eventType' AND upper("PayloadSha256")=permitted->>'payloadHash') THEN
                                RAISE EXCEPTION 'memory_erased_model_immutable';
                            END IF;
                        ELSE RAISE EXCEPTION 'memory_erased_model_immutable'; END IF;
                    END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_erased_model_outbox BEFORE INSERT OR UPDATE ON "ComputeAuditOutbox"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_model_audit();
            CREATE TRIGGER csweet_erased_model_audit BEFORE INSERT OR UPDATE ON "AuditEvents"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_model_audit();
            CREATE TRIGGER csweet_erased_model_payload BEFORE INSERT OR UPDATE ON "AuditEventPayloads"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_model_audit();
            """;
    }
}
