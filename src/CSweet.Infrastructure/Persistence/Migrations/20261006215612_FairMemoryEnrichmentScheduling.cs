using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FairMemoryEnrichmentScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                table: "MemoryCaptureOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemoryEnrichmentProviderLeases",
                columns: table => new
                {
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEnrichmentProviderLeases", x => x.ProviderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEnrichmentProviderLeases_ExpiresAt",
                table: "MemoryEnrichmentProviderLeases",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryEnrichmentProviderLeases");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "MemoryCaptureOutbox");
        }
    }
}
