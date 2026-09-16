using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeAuditTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "ComputeAuditOutbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ComputeAuditOutbox",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "ComputeAuditOutbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ProtectedRequest",
                table: "ComputeAuditOutbox",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceEntityId",
                table: "ComputeAuditOutbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceEntityType",
                table: "ComputeAuditOutbox",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmployeesJson",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceSha256",
                table: "AuditEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditEventEmployees",
                columns: table => new
                {
                    AuditEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEventEmployees", x => new { x.AuditEventId, x.EmployeeId, x.Role });
                    table.ForeignKey(
                        name: "FK_AuditEventEmployees_AuditEvents_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "AuditEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEventPayloads",
                columns: table => new
                {
                    AuditEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtectedContent = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEventPayloads", x => x.AuditEventId);
                    table.ForeignKey(
                        name: "FK_AuditEventPayloads_AuditEvents_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "AuditEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeAuditOutbox_SourceEntityType_SourceEntityId",
                table: "ComputeAuditOutbox",
                columns: new[] { "SourceEntityType", "SourceEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntityType_EntityId",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OrganizationId_OccurredAt_Sequence",
                table: "AuditEvents",
                columns: new[] { "OrganizationId", "OccurredAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEventEmployees_OrganizationId_EmployeeId_AuditEventId",
                table: "AuditEventEmployees",
                columns: new[] { "OrganizationId", "EmployeeId", "AuditEventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEventEmployees");

            migrationBuilder.DropTable(
                name: "AuditEventPayloads");

            migrationBuilder.DropIndex(
                name: "IX_ComputeAuditOutbox_SourceEntityType_SourceEntityId",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_EntityType_EntityId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_OrganizationId_OccurredAt_Sequence",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropColumn(
                name: "ProtectedRequest",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropColumn(
                name: "SourceEntityId",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropColumn(
                name: "SourceEntityType",
                table: "ComputeAuditOutbox");

            migrationBuilder.DropColumn(
                name: "EmployeesJson",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "EvidenceSha256",
                table: "AuditEvents");
        }
    }
}
