using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableResourceCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceCleanupRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceCleanupRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceCleanupRequests_CompletedAt_AvailableAt",
                table: "ResourceCleanupRequests",
                columns: new[] { "CompletedAt", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceCleanupRequests_Kind_ResourceId",
                table: "ResourceCleanupRequests",
                columns: new[] { "Kind", "ResourceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceCleanupRequests");
        }
    }
}
