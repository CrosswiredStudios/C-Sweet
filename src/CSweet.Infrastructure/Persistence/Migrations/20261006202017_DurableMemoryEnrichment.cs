using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableMemoryEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptedExtractionJson",
                table: "MemoryCaptureOutbox",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExtractionAcceptedAt",
                table: "MemoryCaptureOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                table: "MemoryCaptureOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "MemoryCaptureOutbox",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptedExtractionJson",
                table: "MemoryCaptureOutbox");

            migrationBuilder.DropColumn(
                name: "ExtractionAcceptedAt",
                table: "MemoryCaptureOutbox");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "MemoryCaptureOutbox");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "MemoryCaptureOutbox");
        }
    }
}
