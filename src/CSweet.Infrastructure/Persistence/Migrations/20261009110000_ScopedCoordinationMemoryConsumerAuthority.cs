using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace CSweet.Infrastructure.Persistence.Migrations;

public partial class ScopedCoordinationMemoryConsumerAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InstallGuards);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "MemoryCoordinationConsumerAuthority") THEN
                RAISE EXCEPTION 'Cannot discard populated coordination consumer authority history.' USING ERRCODE='23514';
            END IF;
        END $$;
        DROP TRIGGER csweet_memory_coordination_authority ON "AgentCoordinationSessions";
        DROP TRIGGER csweet_memory_coordination_truncate ON "AgentCoordinationSessions";
        DROP TABLE "MemoryCoordinationConsumerAuthority";
        DROP FUNCTION csweet_memory_coordination_authority();
        DROP FUNCTION csweet_memory_coordination_guard();
        DROP FUNCTION csweet_memory_coordination_truncate();
        """);

    public const string InstallGuards = """
        LOCK TABLE "AgentCoordinationSessions" IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE "MemoryCoordinationConsumerAuthority" (
            "Id" uuid PRIMARY KEY, "Revision" bigint NOT NULL DEFAULT 1 CHECK ("Revision">0)
        );
        INSERT INTO "MemoryCoordinationConsumerAuthority" ("Id") SELECT "Id" FROM "AgentCoordinationSessions";
        CREATE FUNCTION csweet_memory_coordination_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF pg_trigger_depth()>1 THEN
                IF TG_OP='INSERT' AND NEW."Revision"=1 THEN RETURN NEW; END IF;
                IF TG_OP='UPDATE' AND NEW."Id"=OLD."Id" AND NEW."Revision"=OLD."Revision"+1 THEN RETURN NEW; END IF;
            END IF;
            RAISE EXCEPTION 'Coordination consumer authority history is immutable.' USING ERRCODE='23514';
        END $$;
        CREATE TRIGGER csweet_memory_coordination_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryCoordinationConsumerAuthority"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_coordination_guard();
        CREATE FUNCTION csweet_memory_coordination_truncate() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            RAISE EXCEPTION 'Coordination consumer authority cannot be truncated.' USING ERRCODE='23514';
        END $$;
        CREATE TRIGGER csweet_memory_coordination_truncate BEFORE TRUNCATE ON "MemoryCoordinationConsumerAuthority"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_coordination_truncate();
        CREATE FUNCTION csweet_memory_coordination_authority() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF TG_OP='INSERT' THEN
                INSERT INTO "MemoryCoordinationConsumerAuthority" ("Id") VALUES (NEW."Id")
                    ON CONFLICT ("Id") DO UPDATE SET "Revision"="MemoryCoordinationConsumerAuthority"."Revision"+1;
                RETURN NEW;
            ELSIF TG_OP='DELETE' THEN
                UPDATE "MemoryCoordinationConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Id"=OLD."Id";
                RETURN OLD;
            END IF;
            IF NEW."Id" IS DISTINCT FROM OLD."Id" THEN
                RAISE EXCEPTION 'Coordination consumer identities cannot change.' USING ERRCODE='23514';
            END IF;
            IF ROW(NEW."OrganizationId",NEW."WorkstreamId",NEW."TeamId",NEW."ConversationId",NEW."SourceKind",
                NEW."SourceBoardId",NEW."SourceWorkItemId",NEW."SourceSprintExecutionId",NEW."SourceStageExecutionId",
                NEW."SourceAssignmentRevision",NEW."InitiatorOrganizationUserId",NEW."InitiatorInstallationId",
                NEW."TargetOrganizationUserId",NEW."TargetInstallationId",NEW."CurrentOrganizationUserId",
                NEW."CurrentAgentWorkItemId",NEW."Status",NEW."Revision",NEW."NextTurnOrdinal",NEW."IsFinalization")
                IS DISTINCT FROM ROW(OLD."OrganizationId",OLD."WorkstreamId",OLD."TeamId",OLD."ConversationId",OLD."SourceKind",
                OLD."SourceBoardId",OLD."SourceWorkItemId",OLD."SourceSprintExecutionId",OLD."SourceStageExecutionId",
                OLD."SourceAssignmentRevision",OLD."InitiatorOrganizationUserId",OLD."InitiatorInstallationId",
                OLD."TargetOrganizationUserId",OLD."TargetInstallationId",OLD."CurrentOrganizationUserId",
                OLD."CurrentAgentWorkItemId",OLD."Status",OLD."Revision",OLD."NextTurnOrdinal",OLD."IsFinalization") THEN
                UPDATE "MemoryCoordinationConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Id"=NEW."Id";
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER csweet_memory_coordination_authority AFTER INSERT OR UPDATE OR DELETE ON "AgentCoordinationSessions"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_coordination_authority();
        CREATE TRIGGER csweet_memory_coordination_truncate BEFORE TRUNCATE ON "AgentCoordinationSessions"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_coordination_truncate();
        """;
}
