using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

public partial class WorkInstructionMemoryLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InstallGuards);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN IF EXISTS(SELECT 1 FROM "WorkInstructionPublications") THEN
            RAISE EXCEPTION 'work_instruction_memory_downgrade_refused' USING ERRCODE='23514';
        END IF;
        IF to_regclass('csweet_memory_episodes') IS NOT NULL THEN
            IF EXISTS(SELECT 1 FROM csweet_memory_episodes WHERE payload->'source'->>'type'='work-instruction') THEN
                RAISE EXCEPTION 'work_instruction_memory_downgrade_refused' USING ERRCODE='23514';
            END IF;
        END IF; END $$;
        DROP TRIGGER work_instruction_memory_invalidate ON "WorkItemComments";
        DROP TRIGGER work_instruction_memory_revision ON "WorkItemComments";
        DROP TRIGGER work_instruction_memory_truncate ON "WorkItemComments";
        DROP FUNCTION work_instruction_memory_invalidate();
        DROP FUNCTION work_instruction_memory_revision();
        """);

    public const string InstallGuards = """
        LOCK TABLE "WorkItemComments" IN SHARE ROW EXCLUSIVE MODE;
        CREATE FUNCTION work_instruction_memory_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP='TRUNCATE' THEN
                IF EXISTS(SELECT 1 FROM "WorkInstructionPublications") THEN
                    RAISE EXCEPTION 'work_instruction_memory_truncate_refused' USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END IF;
            IF OLD."Kind"='human.instruction' THEN
                IF NEW."Id" IS DISTINCT FROM OLD."Id" OR
                    OLD."DeletedAt" IS NOT NULL AND NEW."DeletedAt" IS DISTINCT FROM OLD."DeletedAt" OR
                    (ROW(NEW."Body",NEW."EditedAt",NEW."DeletedAt",NEW."Revision") IS DISTINCT FROM
                     ROW(OLD."Body",OLD."EditedAt",OLD."DeletedAt",OLD."Revision") AND NEW."Revision"<>OLD."Revision"+1) THEN
                    RAISE EXCEPTION 'work_instruction_memory_revision_invalid' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER work_instruction_memory_revision BEFORE UPDATE ON "WorkItemComments"
            FOR EACH ROW EXECUTE FUNCTION work_instruction_memory_revision();
        CREATE TRIGGER work_instruction_memory_truncate BEFORE TRUNCATE ON "WorkItemComments"
            FOR EACH STATEMENT EXECUTE FUNCTION work_instruction_memory_revision();
        CREATE FUNCTION work_instruction_memory_invalidate() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."Kind"<>'human.instruction' OR TG_OP='UPDATE' AND
                ROW(NEW."Body",NEW."EditedAt",NEW."DeletedAt",NEW."Revision") IS NOT DISTINCT FROM
                ROW(OLD."Body",OLD."EditedAt",OLD."DeletedAt",OLD."Revision") THEN RETURN NULL; END IF;
            IF to_regclass('csweet_memory_episodes') IS NOT NULL THEN
                INSERT INTO csweet_memory_suppressions(partition_key,source_type,source_id,episode_id,suppressed_at)
                    SELECT partition_key,'work-instruction',payload->'source'->>'id',id,CURRENT_TIMESTAMP FROM csweet_memory_episodes
                    WHERE payload->'source'->>'type'='work-instruction' AND payload->'metadata'->>'commentId'=OLD."Id"::text
                    ON CONFLICT(partition_key,episode_id) DO NOTHING;
                UPDATE csweet_memory_episodes SET payload=jsonb_set(payload,ARRAY['isSuppressed'],'true'::jsonb)
                    WHERE payload->'source'->>'type'='work-instruction' AND payload->'metadata'->>'commentId'=OLD."Id"::text
                    AND payload->>'isSuppressed' IS DISTINCT FROM 'true';
            END IF;
            RETURN NULL;
        END $$;
        CREATE TRIGGER work_instruction_memory_invalidate AFTER UPDATE OR DELETE ON "WorkItemComments"
            FOR EACH ROW EXECUTE FUNCTION work_instruction_memory_invalidate();
        """;
}
