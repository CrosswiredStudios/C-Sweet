using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationMemoryLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MemorySourceInvalidations_PreviousConversationId",
                table: "MemorySourceInvalidations",
                column: "PreviousConversationId");
            migrationBuilder.Sql("""
                CREATE FUNCTION csweet_conversation_memory_invalidation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP='UPDATE' THEN
                        IF ROW(OLD."Id",OLD."OrganizationId",OLD."AgentOrganizationUserId",OLD."InitiatedByOrganizationUserId",
                               OLD."Kind",OLD."TeamId",OLD."WorkstreamId",OLD."IsPrivate",OLD."ArchivedAt",OLD."MergedIntoConversationId")
                           IS NOT DISTINCT FROM
                           ROW(NEW."Id",NEW."OrganizationId",NEW."AgentOrganizationUserId",NEW."InitiatedByOrganizationUserId",
                               NEW."Kind",NEW."TeamId",NEW."WorkstreamId",NEW."IsPrivate",NEW."ArchivedAt",NEW."MergedIntoConversationId") THEN
                            RETURN NEW;
                        END IF;
                    END IF;

                    -- Preserve source identities before taking the memory writer barrier, in
                    -- the same order as the message trigger. The parent row is already locked.
                    INSERT INTO "MemorySourceInvalidations"
                        ("SourceMessageId","PreviousConversationId","ReasonCode","InvalidatedAt")
                        SELECT "Id",OLD."Id",'memory_conversation_changed',CURRENT_TIMESTAMP
                        FROM "CoreConversationMessages" WHERE "ConversationId"=OLD."Id"
                        ON CONFLICT("SourceMessageId") DO NOTHING;

                    IF to_regclass('csweet_memory_episodes') IS NOT NULL THEN
                        -- Retain identities for older orphaned captures too, including a duplicate
                        -- episode whose source ID differs from its own generated episode ID.
                        INSERT INTO "MemorySourceInvalidations"
                            ("SourceMessageId","PreviousConversationId","ReasonCode","InvalidatedAt")
                            SELECT CASE WHEN lower(payload->'source'->>'id') ~ '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$'
                                    THEN (payload->'source'->>'id')::uuid ELSE id END,
                                OLD."Id",'memory_conversation_changed',CURRENT_TIMESTAMP
                            FROM csweet_memory_episodes
                            WHERE payload->'partition'->>'applicationId'='csweet'
                                AND lower(payload->'source'->>'type') IN ('user','assistant')
                                AND lower(payload->'metadata'->>'conversationId')=OLD."Id"::text
                            ON CONFLICT("SourceMessageId") DO NOTHING;

                        -- Serialize against all memory reviews, even for an empty conversation.
                        -- Reviews use NOWAIT for parent row locks so a writer already holding
                        -- this parent cannot deadlock with their memory-table barrier.
                        LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE;
                        INSERT INTO csweet_memory_suppressions(partition_key,source_type,source_id,episode_id,suppressed_at)
                            SELECT partition_key,lower(payload->'source'->>'type'),payload->'source'->>'id',id,CURRENT_TIMESTAMP
                            FROM csweet_memory_episodes e
                            WHERE payload->'partition'->>'applicationId'='csweet' AND
                                (lower(payload->'metadata'->>'conversationId')=OLD."Id"::text OR
                                 EXISTS(SELECT 1 FROM "MemorySourceInvalidations" s WHERE s."PreviousConversationId"=OLD."Id" AND
                                    (s."SourceMessageId"=e.id OR
                                     (lower(e.payload->'source'->>'type') IN ('user','assistant') AND
                                      s."SourceMessageId"::text=lower(e.payload->'source'->>'id')))))
                            ON CONFLICT(partition_key,episode_id) DO NOTHING;
                        UPDATE csweet_memory_episodes e SET payload=jsonb_set(payload,ARRAY['isSuppressed'],'true'::jsonb)
                            WHERE payload->'partition'->>'applicationId'='csweet' AND
                                (lower(payload->'metadata'->>'conversationId')=OLD."Id"::text OR
                                 EXISTS(SELECT 1 FROM "MemorySourceInvalidations" s WHERE s."PreviousConversationId"=OLD."Id" AND
                                    (s."SourceMessageId"=e.id OR
                                     (lower(e.payload->'source'->>'type') IN ('user','assistant') AND
                                      s."SourceMessageId"::text=lower(e.payload->'source'->>'id')))))
                                AND payload->>'isSuppressed' IS DISTINCT FROM 'true';
                    END IF;
                    IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER csweet_conversation_memory_invalidation AFTER UPDATE OR DELETE ON "CoreConversations"
                    FOR EACH ROW EXECUTE FUNCTION csweet_conversation_memory_invalidation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemorySourceInvalidations") THEN
                        RAISE EXCEPTION 'memory_conversation_lifecycle_downgrade_requires_snapshot';
                    END IF;
                END $$;
                DROP TRIGGER csweet_conversation_memory_invalidation ON "CoreConversations";
                DROP FUNCTION csweet_conversation_memory_invalidation();
                """);
            migrationBuilder.DropIndex(
                name: "IX_MemorySourceInvalidations_PreviousConversationId",
                table: "MemorySourceInvalidations");
        }
    }
}
