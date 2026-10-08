using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthenticatedMemoryTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryTransferReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AppliedEpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryTransferReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryTransferReceipts_OrganizationId_EmployeeId_CreatedAt",
                table: "MemoryTransferReceipts",
                columns: new[] { "OrganizationId", "EmployeeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryTransferReceipts_OrganizationId_OperationId",
                table: "MemoryTransferReceipts",
                columns: new[] { "OrganizationId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryTransferReceipts_PackageId_Action",
                table: "MemoryTransferReceipts",
                columns: new[] { "PackageId", "Action" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryTransferReceipts");
        }
    }
}
