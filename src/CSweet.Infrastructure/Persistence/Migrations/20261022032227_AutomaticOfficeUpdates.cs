using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticOfficeUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticUpdate",
                table: "LocalOfficeSetupSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreparationReadyAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreparationReceiptHash",
                table: "LocalOfficeSetupSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreparationStartedAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResumeAfterUpdate",
                table: "LocalOfficeSetupSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StopStartedAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetVersion",
                table: "LocalOfficeSetupSessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatePhase",
                table: "LocalOfficeSetupSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UseLocalUpdateSource",
                table: "LocalOfficeSetupSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WorkRestoredAt",
                table: "LocalOfficeSetupSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaintenanceInterruptionCount",
                table: "AgentWorkItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "OfficeUpdateJob",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AgentBuildJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentRuntimeInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryBuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NeedsRecovery = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeUpdateJob", x => new { x.SessionId, x.AssignmentId });
                    table.ForeignKey(
                        name: "FK_OfficeUpdateJob_LocalOfficeSetupSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "LocalOfficeSetupSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficeUpdateJob");

            migrationBuilder.DropColumn(
                name: "AutomaticUpdate",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAt",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "PreparationReadyAt",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "PreparationReceiptHash",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "PreparationStartedAt",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "ResumeAfterUpdate",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "StopStartedAt",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "TargetVersion",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "UpdatePhase",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "UseLocalUpdateSource",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "WorkRestoredAt",
                table: "LocalOfficeSetupSessions");

            migrationBuilder.DropColumn(
                name: "MaintenanceInterruptionCount",
                table: "AgentWorkItems");
        }
    }
}
