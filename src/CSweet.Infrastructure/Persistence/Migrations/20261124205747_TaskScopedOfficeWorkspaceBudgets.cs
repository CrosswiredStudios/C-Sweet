using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskScopedOfficeWorkspaceBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_OfficeComputeCapacityRequests",
                table: "OfficeComputeCapacityRequests");

            migrationBuilder.AddColumn<Guid>(
                name: "RequestId",
                table: "OfficeComputeCapacityRequests",
                type: "uuid",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "OfficeComputeCapacityRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceBudgetJson",
                table: "LocalOfficeSetupSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkspaceBudgetJson",
                table: "ExecutionNodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_OfficeComputeCapacityRequests",
                table: "OfficeComputeCapacityRequests",
                columns: new[] { "InstallationId", "ProjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_OfficeComputeCapacityRequests",
                table: "OfficeComputeCapacityRequests");

            // Earlier versions track one wait per employee. Keep the oldest one on downgrade.
            migrationBuilder.Sql("""
                DELETE FROM "OfficeComputeCapacityRequests" a USING "OfficeComputeCapacityRequests" b
                WHERE a."InstallationId" = b."InstallationId" AND
                    (a."RequestedAt", a."ProjectId") > (b."RequestedAt", b."ProjectId")
                """);

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "OfficeComputeCapacityRequests");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "OfficeComputeCapacityRequests");

            migrationBuilder.DropColumn(
                name: "WorkspaceBudgetJson",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "WorkspaceBudgetJson",
                table: "ExecutionNodes");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OfficeComputeCapacityRequests",
                table: "OfficeComputeCapacityRequests",
                column: "InstallationId");
        }
    }
}
