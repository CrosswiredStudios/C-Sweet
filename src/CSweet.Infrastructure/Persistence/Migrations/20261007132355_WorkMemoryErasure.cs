using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkMemoryErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MemoryErasedAt",
                table: "AgentWorkItems",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.Sql(InstallTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "AgentWorkItems" WHERE "MemoryErasedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'memory_work_erasure_downgrade_requires_snapshot';
                    END IF;
                END $$;
                DROP TRIGGER csweet_erased_work_immutable ON "AgentWorkItems";
                DROP TRIGGER csweet_erased_work_attempt ON "AgentWorkAttempts";
                DROP TRIGGER csweet_erased_work_progress ON "AgentWorkProgress";
                DROP FUNCTION csweet_erased_work_immutable();
                DROP FUNCTION csweet_erased_work_child_guard();
                """);
            migrationBuilder.DropColumn(
                name: "MemoryErasedAt",
                table: "AgentWorkItems");
        }

        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_erased_work_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."MemoryErasedAt" IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'memory_erased_work_immutable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_erased_work_immutable BEFORE UPDATE ON "AgentWorkItems"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_work_immutable();

            CREATE FUNCTION csweet_erased_work_child_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE erased timestamptz;
            BEGIN
                SELECT "MemoryErasedAt" INTO erased FROM "AgentWorkItems" WHERE "Id"=NEW."AgentWorkItemId" FOR UPDATE;
                IF erased IS NOT NULL THEN RAISE EXCEPTION 'memory_erased_work_immutable'; END IF;
                IF TG_OP='UPDATE' AND OLD."AgentWorkItemId" IS DISTINCT FROM NEW."AgentWorkItemId" THEN
                    SELECT "MemoryErasedAt" INTO erased FROM "AgentWorkItems" WHERE "Id"=OLD."AgentWorkItemId" FOR UPDATE;
                    IF erased IS NOT NULL THEN RAISE EXCEPTION 'memory_erased_work_immutable'; END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_erased_work_attempt BEFORE INSERT OR UPDATE ON "AgentWorkAttempts"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_work_child_guard();
            CREATE TRIGGER csweet_erased_work_progress BEFORE INSERT OR UPDATE ON "AgentWorkProgress"
                FOR EACH ROW EXECUTE FUNCTION csweet_erased_work_child_guard();
            """;
    }
}
