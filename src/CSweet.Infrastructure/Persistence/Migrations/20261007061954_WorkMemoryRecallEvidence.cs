using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkMemoryRecallEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MemoryRecallReceiptJson",
                table: "AgentWorkItems",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Old binaries cannot validate recalled context in outstanding work.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "AgentWorkItems" WHERE "MemoryRecallReceiptJson" IS NOT NULL) THEN
                        RAISE EXCEPTION 'memory_recall_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "MemoryRecallReceiptJson",
                table: "AgentWorkItems");
        }
    }
}
