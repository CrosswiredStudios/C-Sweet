using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NativeWorkInputOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NativeWorkInputReceiptJson",
                table: "AgentWorkItems",
                type: "character varying(32768)",
                maxLength: 32768,
                nullable: true);
            migrationBuilder.Sql(InstallGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RemoveGuards);
            migrationBuilder.DropColumn(
                name: "NativeWorkInputReceiptJson",
                table: "AgentWorkItems");
        }

        internal const string RemoveGuards = """
            DO $$ BEGIN
                IF EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "NativeWorkInputReceiptJson" IS NOT NULL) THEN
                    RAISE EXCEPTION 'memory_native_input_downgrade_requires_snapshot';
                END IF;
            END $$;
            DROP TRIGGER csweet_native_work_input_binding ON "AgentWorkItems";
            DROP TRIGGER csweet_native_work_input_truncate ON "AgentWorkItems";
            DROP FUNCTION csweet_native_work_input_binding();
            DROP FUNCTION csweet_native_work_input_truncate();
            """;

        internal const string InstallGuards = """
            CREATE FUNCTION csweet_native_work_input_binding() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE proof jsonb; origins jsonb;
            BEGIN
                IF TG_OP='UPDATE' THEN
                    IF NEW."NativeWorkInputReceiptJson" IS DISTINCT FROM OLD."NativeWorkInputReceiptJson" THEN
                        RAISE EXCEPTION 'memory_native_input_immutable';
                    END IF;
                    IF OLD."NativeWorkInputReceiptJson" IS NOT NULL THEN
                        IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."OrganizationId" IS DISTINCT FROM OLD."OrganizationId" OR
                           NEW."AgentInstallationId" IS DISTINCT FROM OLD."AgentInstallationId" OR NEW."Kind" IS DISTINCT FROM OLD."Kind" OR
                           NEW."PayloadHash" IS DISTINCT FROM OLD."PayloadHash" THEN
                            RAISE EXCEPTION 'memory_native_input_immutable';
                        END IF;
                        IF NEW."ProtectedPayload" IS DISTINCT FROM OLD."ProtectedPayload" OR NEW."Name" IS DISTINCT FROM OLD."Name" OR
                           NEW."CorrelationId" IS DISTINCT FROM OLD."CorrelationId" OR NEW."CausationId" IS DISTINCT FROM OLD."CausationId" OR
                           NEW."SourceType" IS DISTINCT FROM OLD."SourceType" OR NEW."SourceId" IS DISTINCT FROM OLD."SourceId" OR
                           NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey" THEN
                            IF NOT (OLD."MemoryErasedAt" IS NULL AND NEW."MemoryErasedAt" IS NOT NULL AND NEW."Name"='memory.erased' AND
                                    NEW."SourceType" IS NULL AND NEW."SourceId" IS NULL AND NEW."CausationId" IS NULL AND
                                    NEW."CorrelationId"=NEW."Id"::text AND NEW."IdempotencyKey"='memory-erased:' ||
                                    upper(encode(sha256(convert_to(OLD."IdempotencyKey",'UTF8')),'hex'))) THEN
                                RAISE EXCEPTION 'memory_native_input_immutable';
                            END IF;
                        END IF;
                    END IF;
                    RETURN NEW;
                END IF;
                IF NEW."NativeWorkInputReceiptJson" IS NULL THEN RETURN NEW; END IF;
                proof := NEW."NativeWorkInputReceiptJson"::jsonb;
                origins := proof->'origins';
                IF jsonb_typeof(proof) IS DISTINCT FROM 'object' OR jsonb_typeof(origins) IS DISTINCT FROM 'object' OR
                   NOT (proof ?& ARRAY['version','builder','workId','organizationId','installationId','payloadHash','protectedPayloadHash',
                       'kind','name','correlationId','causationId','sourceType','sourceId','idempotencyHash','origins','coverage']) OR
                   NOT (origins ?& ARRAY['caseId','boardId','stageId','sprintId','assignmentRevision','sessionId','conversationId',
                       'sessionRevision','turnOrdinal','contributors','turnIds']) OR
                   (SELECT count(*) FROM json_each(NEW."NativeWorkInputReceiptJson"::json))<>16 OR
                   (SELECT count(DISTINCT key) FROM json_each(NEW."NativeWorkInputReceiptJson"::json))<>16 OR
                   (SELECT count(*) FROM json_each(NEW."NativeWorkInputReceiptJson"::json->'origins'))<>11 OR
                   (SELECT count(DISTINCT key) FROM json_each(NEW."NativeWorkInputReceiptJson"::json->'origins'))<>11 OR
                   proof->>'version' IS DISTINCT FROM '1' OR proof->>'builder' IS DISTINCT FROM 'canonical-case-coordination-v1' OR
                   proof->>'coverage' IS DISTINCT FROM 'input-origins-only-v1' OR NEW."MemoryErasedAt" IS NOT NULL OR
                   NEW."MemoryRecallReceiptJson" IS NOT NULL OR NEW."Kind"<>'Event' OR NEW."SourceType" IS DISTINCT FROM 'agent-coordination' OR
                   NEW."Name" IS DISTINCT FROM 'com.csweet.agent.coordination.turn-requested.v1' OR
                   proof->>'workId' IS DISTINCT FROM NEW."Id"::text OR proof->>'organizationId' IS DISTINCT FROM NEW."OrganizationId" OR
                   proof->>'installationId' IS DISTINCT FROM NEW."AgentInstallationId"::text OR
                   proof->>'payloadHash' IS DISTINCT FROM NEW."PayloadHash" OR length(NEW."PayloadHash")<>64 OR
                   proof->>'protectedPayloadHash' IS DISTINCT FROM upper(encode(sha256(NEW."ProtectedPayload"),'hex')) OR
                   proof->>'kind' IS DISTINCT FROM NEW."Kind" OR proof->>'name' IS DISTINCT FROM NEW."Name" OR
                   proof->>'correlationId' IS DISTINCT FROM NEW."CorrelationId" OR proof->>'causationId' IS DISTINCT FROM NEW."CausationId" OR
                   proof->>'sourceType' IS DISTINCT FROM NEW."SourceType" OR proof->>'sourceId' IS DISTINCT FROM NEW."SourceId" OR
                   proof->>'idempotencyHash' IS DISTINCT FROM upper(encode(sha256(convert_to(NEW."IdempotencyKey",'UTF8')),'hex')) OR
                   origins->>'sessionId' IS DISTINCT FROM NEW."CorrelationId" OR
                   NEW."SourceId" IS NULL OR NEW."SourceId"::uuid='00000000-0000-0000-0000-000000000000' OR
                   (origins->>'sessionRevision') IS NULL OR (origins->>'sessionRevision')::bigint<1 OR
                   (origins->>'assignmentRevision') IS NULL OR (origins->>'assignmentRevision')::bigint<0 OR
                   (origins->>'turnOrdinal') IS NULL OR (origins->>'turnOrdinal')::integer<1 OR
                   jsonb_typeof(origins->'contributors') IS DISTINCT FROM 'array' OR jsonb_array_length(origins->'contributors')<>2 OR
                   jsonb_typeof(origins->'turnIds') IS DISTINCT FROM 'array' OR jsonb_array_length(origins->'turnIds') NOT BETWEEN 1 AND 64 THEN
                    RAISE EXCEPTION 'memory_native_input_binding_invalid';
                END IF;
                IF EXISTS(SELECT 1 FROM jsonb_each_text(origins) WHERE key IN
                    ('caseId','boardId','stageId','sprintId','sessionId','conversationId') AND
                    (value IS NULL OR value::uuid='00000000-0000-0000-0000-000000000000')) OR
                   (SELECT count(DISTINCT value::text) FROM jsonb_array_elements(origins->'contributors'))<>2 OR
                   (SELECT count(DISTINCT value::text) FROM jsonb_array_elements(origins->'turnIds'))<>jsonb_array_length(origins->'turnIds') OR
                   EXISTS(SELECT 1 FROM jsonb_array_elements_text(origins->'contributors') x(value) WHERE value IS NULL OR value::uuid='00000000-0000-0000-0000-000000000000') OR
                   EXISTS(SELECT 1 FROM jsonb_array_elements_text(origins->'turnIds') x(value) WHERE value IS NULL OR value::uuid='00000000-0000-0000-0000-000000000000') THEN
                    RAISE EXCEPTION 'memory_native_input_binding_invalid';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_native_work_input_binding BEFORE INSERT OR UPDATE ON "AgentWorkItems"
                FOR EACH ROW EXECUTE FUNCTION csweet_native_work_input_binding();
            CREATE FUNCTION csweet_native_work_input_truncate() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "NativeWorkInputReceiptJson" IS NOT NULL) THEN
                    RAISE EXCEPTION 'memory_native_input_downgrade_requires_snapshot';
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER csweet_native_work_input_truncate BEFORE TRUNCATE ON "AgentWorkItems"
                FOR EACH STATEMENT EXECUTE FUNCTION csweet_native_work_input_truncate();
            """;
    }
}
