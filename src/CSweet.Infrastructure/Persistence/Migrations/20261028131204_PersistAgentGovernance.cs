using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistAgentGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TrackedDelegationId",
                table: "Delegations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GovernanceRevision",
                table: "AgentInstallations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Posture",
                table: "AgentInstallations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PostureChangedAt",
                table: "AgentInstallations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PostureChangedBy",
                table: "AgentInstallations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentCapabilityApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Capability = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Risk = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    PostureRevision = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ResultsJson = table.Column<string>(type: "text", nullable: true),
                    DecidedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCapabilityApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCapabilityApprovals_AgentInstallations_AgentInstallati~",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityApprovals_AgentInstallationId",
                table: "AgentCapabilityApprovals",
                column: "AgentInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityApprovals_OperationKey",
                table: "AgentCapabilityApprovals",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCapabilityApprovals_OrganizationId_Status_CreatedAt",
                table: "AgentCapabilityApprovals",
                columns: new[] { "OrganizationId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCapabilityApprovals");

            migrationBuilder.DropColumn(
                name: "TrackedDelegationId",
                table: "Delegations");

            migrationBuilder.DropColumn(
                name: "GovernanceRevision",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "Posture",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "PostureChangedAt",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "PostureChangedBy",
                table: "AgentInstallations");
        }
    }
}
