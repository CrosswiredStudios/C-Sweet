using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewedMemoryErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MemoryErasedAt",
                table: "ChatTurns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryErasureReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InventoryJson = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    ErasedRecords = table.Column<int>(type: "integer", nullable: false),
                    ErasedRevisions = table.Column<int>(type: "integer", nullable: false),
                    ClearedJobs = table.Column<int>(type: "integer", nullable: false),
                    ClearedWorks = table.Column<int>(type: "integer", nullable: false),
                    ClearedDiagnosticTurns = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryErasureReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryErasureReceipts_OrganizationId_EmployeeId_CreatedAt",
                table: "MemoryErasureReceipts",
                columns: new[] { "OrganizationId", "EmployeeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryErasureReceipts_OrganizationId_OperationId",
                table: "MemoryErasureReceipts",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true);
            migrationBuilder.Sql(InstallTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemoryErasureReceipts") OR EXISTS(SELECT 1 FROM "ChatTurns" WHERE "MemoryErasedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'memory_reviewed_erasure_downgrade_requires_snapshot';
                    END IF;
                END $$;
                DROP TRIGGER csweet_memory_erasure_receipt_immutable ON "MemoryErasureReceipts";
                DROP TRIGGER csweet_memory_erased_chat ON "ChatTurns";
                DROP TRIGGER csweet_memory_erased_trace ON "ChatTurnTraceEvents";
                DROP FUNCTION csweet_memory_erasure_receipt_immutable();
                DROP FUNCTION csweet_memory_erased_chat();
                DROP FUNCTION csweet_memory_erased_trace();
                """);
            migrationBuilder.DropTable(
                name: "MemoryErasureReceipts");

            migrationBuilder.DropColumn(
                name: "MemoryErasedAt",
                table: "ChatTurns");
        }

        internal const string InstallTriggers = """
            CREATE FUNCTION csweet_memory_erasure_receipt_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'memory_erasure_receipt_immutable'; END $$;
            CREATE TRIGGER csweet_memory_erasure_receipt_immutable BEFORE UPDATE OR DELETE ON "MemoryErasureReceipts"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_erasure_receipt_immutable();

            CREATE FUNCTION csweet_memory_erased_chat() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF TG_OP='INSERT' THEN
                    IF NEW."MemoryErasedAt" IS NOT NULL OR EXISTS(SELECT 1 FROM "MemoryErasureReceipts" r
                        WHERE r."InventoryJson"::jsonb->'turnIds' @> jsonb_build_array(NEW."Id")) THEN
                        RAISE EXCEPTION 'memory_erased_chat_immutable';
                    END IF;
                ELSIF NEW."Id" IS DISTINCT FROM OLD."Id" OR OLD."MemoryErasedAt" IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
                    RAISE EXCEPTION 'memory_erased_chat_immutable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_memory_erased_chat BEFORE INSERT OR UPDATE ON "ChatTurns"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_erased_chat();

            CREATE FUNCTION csweet_memory_erased_trace() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE erased timestamptz;
            BEGIN
                SELECT "MemoryErasedAt" INTO erased FROM "ChatTurns" WHERE "Id"=NEW."ChatTurnId" FOR SHARE;
                IF erased IS NOT NULL OR EXISTS(SELECT 1 FROM "MemoryErasureReceipts" r
                    WHERE r."InventoryJson"::jsonb->'turnIds' @> jsonb_build_array(NEW."ChatTurnId")) THEN
                    RAISE EXCEPTION 'memory_erased_chat_immutable';
                END IF;
                IF TG_OP='UPDATE' AND OLD."ChatTurnId" IS DISTINCT FROM NEW."ChatTurnId" THEN
                    SELECT "MemoryErasedAt" INTO erased FROM "ChatTurns" WHERE "Id"=OLD."ChatTurnId" FOR SHARE;
                    IF erased IS NOT NULL THEN RAISE EXCEPTION 'memory_erased_chat_immutable'; END IF;
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_memory_erased_trace BEFORE INSERT OR UPDATE ON "ChatTurnTraceEvents"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_erased_trace();
            """;
    }
}
