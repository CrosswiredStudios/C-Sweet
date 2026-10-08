using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemorySourceReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemorySourceReconciliationCheckpoints",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastEpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScannedEpisodes = table.Column<long>(type: "bigint", nullable: false),
                    SuppressedEpisodes = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemorySourceReconciliationCheckpoints", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS(SELECT 1 FROM "MemorySourceReconciliationCheckpoints" WHERE "ScannedEpisodes">0 OR "SuppressedEpisodes">0) THEN
                        RAISE EXCEPTION 'memory_reconciliation_downgrade_requires_snapshot';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "MemorySourceReconciliationCheckpoints");
        }
    }
}
