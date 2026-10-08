using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationMemoryInvalidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemorySourceInvalidations",
                columns: table => new
                {
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InvalidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemorySourceInvalidations", x => x.SourceMessageId);
                });
            migrationBuilder.Sql(InstallTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemorySourceInvalidations") THEN
                        RAISE EXCEPTION 'memory_source_invalidation_downgrade_requires_snapshot';
                    END IF;
                END $$;
                DROP TRIGGER csweet_message_memory_invalidation ON "CoreConversationMessages";
                DROP FUNCTION csweet_message_memory_invalidation();
                DROP TRIGGER csweet_source_invalidation_immutable ON "MemorySourceInvalidations";
                DROP FUNCTION csweet_source_invalidation_immutable();
                """);
            migrationBuilder.DropTable(
                name: "MemorySourceInvalidations");
        }

        // Keep this upgrade self-contained: later lifecycle changes must get a new migration.
        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_source_invalidation_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'memory_source_invalidation_immutable';
            END $$;
            CREATE TRIGGER csweet_source_invalidation_immutable BEFORE UPDATE OR DELETE ON "MemorySourceInvalidations"
                FOR EACH ROW EXECUTE FUNCTION csweet_source_invalidation_immutable();

            CREATE FUNCTION csweet_message_memory_invalidation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP='UPDATE' THEN
                    IF ROW(OLD."Id", OLD."Content", OLD."Role", OLD."CreatedAt", OLD."ConversationId",
                           OLD."SenderOrganizationUserId") IS NOT DISTINCT FROM
                       ROW(NEW."Id", NEW."Content", NEW."Role", NEW."CreatedAt", NEW."ConversationId",
                           NEW."SenderOrganizationUserId") AND
                       (OLD."ChatTurnId" IS NULL OR OLD."ChatTurnId" IS NOT DISTINCT FROM NEW."ChatTurnId") THEN
                        RETURN NEW;
                    END IF;
                END IF;

                INSERT INTO "MemorySourceInvalidations"
                    ("SourceMessageId", "PreviousConversationId", "ReasonCode", "InvalidatedAt")
                VALUES (OLD."Id", OLD."ConversationId",
                    CASE WHEN TG_OP='DELETE' THEN 'memory_source_deleted' ELSE 'memory_source_edited' END,
                    CURRENT_TIMESTAMP)
                ON CONFLICT ("SourceMessageId") DO NOTHING;

                -- Platform migrations may precede first-time library initialization. No source
                -- exists yet in that case; the durable marker still prevents later capture.
                IF to_regclass('csweet_memory_episodes') IS NOT NULL THEN
                    -- Match capture/apply lock order: original message, then memory episodes.
                    -- Wait for source readers before taking row locks or blocking their writes.
                    LOCK TABLE csweet_memory_episodes IN EXCLUSIVE MODE;
                    IF EXISTS(SELECT 1 FROM csweet_memory_episodes
                        WHERE payload->'partition'->>'applicationId'='csweet'
                            AND (id=OLD."Id" OR
                                (lower(payload->'source'->>'type') IN ('user','assistant') AND
                                 lower(payload->'source'->>'id')=OLD."Id"::text))) THEN
                        -- Existing stores require the matching suppression-capable library.
                        -- A missing table fails the edit transaction closed until it is upgraded.
                        INSERT INTO csweet_memory_suppressions(partition_key,source_type,source_id,episode_id,suppressed_at)
                            SELECT partition_key,lower(payload->'source'->>'type'),payload->'source'->>'id',id,CURRENT_TIMESTAMP
                            FROM csweet_memory_episodes
                            WHERE payload->'partition'->>'applicationId'='csweet'
                                AND (id=OLD."Id" OR
                                    (lower(payload->'source'->>'type') IN ('user','assistant') AND
                                     lower(payload->'source'->>'id')=OLD."Id"::text))
                            ON CONFLICT(partition_key,episode_id) DO NOTHING;
                        UPDATE csweet_memory_episodes
                            SET payload=jsonb_set(payload,ARRAY['isSuppressed'],'true'::jsonb)
                            WHERE payload->'partition'->>'applicationId'='csweet'
                                AND (id=OLD."Id" OR
                                    (lower(payload->'source'->>'type') IN ('user','assistant') AND
                                     lower(payload->'source'->>'id')=OLD."Id"::text))
                                AND payload->>'isSuppressed' IS DISTINCT FROM 'true';
                    END IF;
                END IF;
                IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_message_memory_invalidation AFTER UPDATE OR DELETE ON "CoreConversationMessages"
                FOR EACH ROW EXECUTE FUNCTION csweet_message_memory_invalidation();
            """;
    }
}
