using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInferenceAnalyticsDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentInstallationId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EmployeeId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "AgentRunLogs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "AgentRunLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_OrganizationId_EmployeeId_StartedAt",
                table: "AgentRunLogs",
                columns: new[] { "OrganizationId", "EmployeeId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRunLogs_OrganizationId_StartedAt",
                table: "AgentRunLogs",
                columns: new[] { "OrganizationId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_OrganizationId_EmployeeId_StartedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropIndex(
                name: "IX_AgentRunLogs_OrganizationId_StartedAt",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "AgentInstallationId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "EmployeeId",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "AgentRunLogs");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "AgentRunLogs");
        }
    }
}
