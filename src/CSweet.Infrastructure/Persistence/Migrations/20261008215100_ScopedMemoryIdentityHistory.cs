using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

/// <summary>Deleted audience identities must never inherit retained memories or receipts.</summary>
public partial class ScopedMemoryIdentityHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(IdentityHistory);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "MemoryScopeIdentityHistory" WHERE "DeletedAt" IS NOT NULL)
                OR EXISTS (SELECT 1 FROM "AgentMemoryReadReceipts"
                    WHERE "EvidenceJson" LIKE '%"scopedAuthorityHash"%') THEN
                RAISE EXCEPTION 'Scoped identity history has been used; downgrading would allow deleted audience identities to be reused.';
            END IF;
        END $$;
        DROP TRIGGER csweet_memory_scope_identity ON "CoreConversations";
        DROP TRIGGER csweet_memory_scope_identity ON "CoreWorkTasks";
        DROP TRIGGER csweet_memory_scope_identity ON "WorkBoards";
        DROP TRIGGER csweet_memory_scope_identity_delete ON "CoreConversations";
        DROP TRIGGER csweet_memory_scope_identity_delete ON "CoreWorkTasks";
        DROP TRIGGER csweet_memory_scope_identity_delete ON "WorkBoards";
        DROP TRIGGER csweet_memory_scope_identity_truncate ON "CoreConversations";
        DROP TRIGGER csweet_memory_scope_identity_truncate ON "CoreWorkTasks";
        DROP TRIGGER csweet_memory_scope_identity_truncate ON "WorkBoards";
        DROP TABLE "MemoryScopeIdentityHistory";
        DROP FUNCTION csweet_memory_scope_identity();
        DROP FUNCTION csweet_memory_scope_identity_history_guard();
        DROP FUNCTION csweet_memory_scope_identity_truncate();
        """);

    // No tenant, participant or memory content is stored here. There is deliberately no
    // parent FK: deletion, including an organization cascade, must preserve the fence.
    public const string IdentityHistory = """
        LOCK TABLE "CoreConversations","CoreWorkTasks","WorkBoards" IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE "MemoryScopeIdentityHistory" (
            "ResourceKind" text NOT NULL CHECK ("ResourceKind" IN ('Conversation','Case','Board')),
            "ResourceId" uuid NOT NULL,
            "DeletedAt" timestamptz NULL,
            PRIMARY KEY ("ResourceKind","ResourceId")
        );
        INSERT INTO "MemoryScopeIdentityHistory" ("ResourceKind","ResourceId")
            SELECT 'Conversation',"Id" FROM "CoreConversations"
            UNION ALL SELECT 'Case',"Id" FROM "CoreWorkTasks"
            UNION ALL SELECT 'Board',"Id" FROM "WorkBoards";

        -- Existing memory history can outlive its parent. Fence its native scope IDs
        -- as well, reading partition metadata only. The library schema is optional
        -- on installations that have never initialized memory storage.
        DO $$ BEGIN
            IF to_regclass('csweet_memory_revisions') IS NOT NULL THEN
                INSERT INTO "MemoryScopeIdentityHistory" ("ResourceKind","ResourceId","DeletedAt")
                SELECT resource_kind,resource_id,clock_timestamp() FROM (SELECT DISTINCT resource_kind,resource_id FROM (
                    SELECT 'Conversation' AS resource_kind,(payload->'partition'->>'conversationId')::uuid AS resource_id
                    FROM csweet_memory_revisions
                    WHERE payload->'partition'->>'applicationId'='csweet'
                        AND payload->'partition'->>'agentId' IS NULL AND payload->'partition'->>'userId' IS NULL
                        AND payload->'partition'->>'customNamespace' IS NULL
                        AND payload->'partition'->>'conversationId' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                    UNION ALL SELECT 'Case',substring(payload->'partition'->>'customNamespace' FROM 6)::uuid
                    FROM csweet_memory_revisions
                    WHERE payload->'partition'->>'applicationId'='csweet'
                        AND payload->'partition'->>'agentId' IS NULL AND payload->'partition'->>'userId' IS NULL
                        AND payload->'partition'->>'conversationId' IS NULL
                        AND payload->'partition'->>'customNamespace' ~ '^case:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                ) scopes) identities ON CONFLICT DO NOTHING;
            END IF;
        END $$;

        CREATE FUNCTION csweet_memory_scope_identity_history_guard() RETURNS trigger AS $$
        BEGIN
            IF pg_trigger_depth()>1 THEN
                IF TG_OP='INSERT' THEN RETURN NEW; END IF;
                IF TG_OP='UPDATE' AND OLD."DeletedAt" IS NULL AND NEW."DeletedAt" IS NOT NULL
                    AND ROW(NEW."ResourceKind",NEW."ResourceId") = ROW(OLD."ResourceKind",OLD."ResourceId") THEN
                    RETURN NEW;
                END IF;
            END IF;
            RAISE EXCEPTION 'Memory scope identity history is immutable.' USING ERRCODE='23514';
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_scope_identity_history_guard
            BEFORE INSERT OR UPDATE OR DELETE ON "MemoryScopeIdentityHistory"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity_history_guard();

        CREATE FUNCTION csweet_memory_scope_identity() RETURNS trigger AS $$
        DECLARE deleted_at timestamptz;
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF NEW."Id" IS DISTINCT FROM OLD."Id" THEN
                    RAISE EXCEPTION 'Memory audience identities cannot be changed.' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            ELSIF TG_OP='DELETE' THEN
                UPDATE "MemoryScopeIdentityHistory" SET "DeletedAt"=clock_timestamp()
                    WHERE "ResourceKind"=TG_ARGV[0] AND "ResourceId"=OLD."Id";
                RETURN OLD;
            END IF;
            INSERT INTO "MemoryScopeIdentityHistory" ("ResourceKind","ResourceId")
                VALUES (TG_ARGV[0],NEW."Id") ON CONFLICT DO NOTHING;
            -- This row lock serializes a concurrent delete/reinsert with its durable fence.
            SELECT "DeletedAt" INTO deleted_at FROM "MemoryScopeIdentityHistory"
                WHERE "ResourceKind"=TG_ARGV[0] AND "ResourceId"=NEW."Id" FOR UPDATE;
            IF deleted_at IS NOT NULL THEN
                RAISE EXCEPTION 'Deleted memory audience identities cannot be reused; create a new identity.' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END; $$ LANGUAGE plpgsql;

        CREATE FUNCTION csweet_memory_scope_identity_truncate() RETURNS trigger AS $$
        BEGIN
            RAISE EXCEPTION 'Truncation would discard memory audience identity history; use ordinary deletion.' USING ERRCODE='23514';
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_scope_identity_truncate BEFORE TRUNCATE ON "MemoryScopeIdentityHistory"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_scope_identity_truncate();

        CREATE TRIGGER csweet_memory_scope_identity BEFORE INSERT OR UPDATE ON "CoreConversations"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity('Conversation');
        CREATE TRIGGER csweet_memory_scope_identity_delete AFTER DELETE ON "CoreConversations"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity('Conversation');
        CREATE TRIGGER csweet_memory_scope_identity_truncate BEFORE TRUNCATE ON "CoreConversations"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_scope_identity_truncate();
        CREATE TRIGGER csweet_memory_scope_identity BEFORE INSERT OR UPDATE ON "CoreWorkTasks"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity('Case');
        CREATE TRIGGER csweet_memory_scope_identity_delete AFTER DELETE ON "CoreWorkTasks"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity('Case');
        CREATE TRIGGER csweet_memory_scope_identity_truncate BEFORE TRUNCATE ON "CoreWorkTasks"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_scope_identity_truncate();
        CREATE TRIGGER csweet_memory_scope_identity BEFORE INSERT OR UPDATE ON "WorkBoards"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity('Board');
        CREATE TRIGGER csweet_memory_scope_identity_delete AFTER DELETE ON "WorkBoards"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_scope_identity('Board');
        CREATE TRIGGER csweet_memory_scope_identity_truncate BEFORE TRUNCATE ON "WorkBoards"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_scope_identity_truncate();
        """;
}
