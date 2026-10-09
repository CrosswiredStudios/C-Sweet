using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedCaseMemoryAudienceAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MemoryAudienceRevision",
                table: "WorkBoards",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "MemoryAudienceRevision",
                table: "CoreWorkTasks",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
            migrationBuilder.Sql(AuthorityTriggers);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "CoreWorkTasks" WHERE "MemoryAudienceRevision">1)
                        OR EXISTS (SELECT 1 FROM "WorkBoards" WHERE "MemoryAudienceRevision">1)
                        OR EXISTS (SELECT 1 FROM "AgentMemoryReadReceipts"
                            WHERE "EvidenceJson" LIKE '%"scopedAuthorityHash"%') THEN
                        RAISE EXCEPTION 'Scoped case authority has been used; downgrading would discard revocation history.';
                    END IF;
                END $$;
                DROP TRIGGER IF EXISTS csweet_memory_case_authority ON "CoreWorkTasks";
                DROP TRIGGER IF EXISTS csweet_memory_board_authority ON "WorkBoards";
                DROP FUNCTION IF EXISTS csweet_memory_case_authority();
                DROP FUNCTION IF EXISTS csweet_memory_board_authority();
                """);
            migrationBuilder.DropColumn(
                name: "MemoryAudienceRevision",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "MemoryAudienceRevision",
                table: "CoreWorkTasks");
        }

        public const string AuthorityTriggers = """
            CREATE OR REPLACE FUNCTION csweet_memory_case_authority() RETURNS trigger AS $$
            BEGIN
                IF ROW(NEW."OrganizationId",NEW."BoardId",NEW."ArchivedAt",NEW."AssignedEmployeeId",NEW."AssignedAgentInstallationId",NEW."ClaimEventId",NEW."PlanningRevision")
                    IS DISTINCT FROM ROW(OLD."OrganizationId",OLD."BoardId",OLD."ArchivedAt",OLD."AssignedEmployeeId",OLD."AssignedAgentInstallationId",OLD."ClaimEventId",OLD."PlanningRevision")
                    OR NEW."MemoryAudienceRevision" IS DISTINCT FROM OLD."MemoryAudienceRevision" THEN
                    NEW."MemoryAudienceRevision" := OLD."MemoryAudienceRevision" + 1;
                ELSE NEW."MemoryAudienceRevision" := OLD."MemoryAudienceRevision";
                END IF;
                RETURN NEW;
            END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER csweet_memory_case_authority BEFORE UPDATE ON "CoreWorkTasks"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_authority();

            CREATE OR REPLACE FUNCTION csweet_memory_board_authority() RETURNS trigger AS $$
            BEGIN
                IF ROW(NEW."OrganizationId",NEW."Kind",NEW."OwnerOrganizationUserId",NEW."TeamId",NEW."ArchivedAt",NEW."WorkstreamId",NEW."ManagerOrganizationUserId")
                    IS DISTINCT FROM ROW(OLD."OrganizationId",OLD."Kind",OLD."OwnerOrganizationUserId",OLD."TeamId",OLD."ArchivedAt",OLD."WorkstreamId",OLD."ManagerOrganizationUserId")
                    OR NEW."MemoryAudienceRevision" IS DISTINCT FROM OLD."MemoryAudienceRevision" THEN
                    NEW."MemoryAudienceRevision" := OLD."MemoryAudienceRevision" + 1;
                ELSE NEW."MemoryAudienceRevision" := OLD."MemoryAudienceRevision";
                END IF;
                RETURN NEW;
            END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER csweet_memory_board_authority BEFORE UPDATE ON "WorkBoards"
                FOR EACH ROW EXECUTE FUNCTION csweet_memory_board_authority();
            """;
    }
}
