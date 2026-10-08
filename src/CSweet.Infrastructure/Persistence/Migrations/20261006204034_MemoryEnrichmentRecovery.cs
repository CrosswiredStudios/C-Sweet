using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MemoryEnrichmentRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RetryGeneration",
                table: "MemoryCaptureOutbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MemoryCaptureRetryReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousGeneration = table.Column<int>(type: "integer", nullable: false),
                    RetryGeneration = table.Column<int>(type: "integer", nullable: false),
                    PreviousAttempts = table.Column<int>(type: "integer", nullable: false),
                    ReusesAcceptedExtraction = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryCaptureRetryReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCaptureRetryReceipts_JobId_OperationId",
                table: "MemoryCaptureRetryReceipts",
                columns: new[] { "JobId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCaptureRetryReceipts_JobId_RetryGeneration",
                table: "MemoryCaptureRetryReceipts",
                columns: new[] { "JobId", "RetryGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryCaptureRetryReceipts_OrganizationId_EmployeeId_Create~",
                table: "MemoryCaptureRetryReceipts",
                columns: new[] { "OrganizationId", "EmployeeId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryCaptureRetryReceipts");

            migrationBuilder.DropColumn(
                name: "RetryGeneration",
                table: "MemoryCaptureOutbox");
        }
    }
}
