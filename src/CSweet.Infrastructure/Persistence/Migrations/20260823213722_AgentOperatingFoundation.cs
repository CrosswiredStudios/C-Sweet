using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentOperatingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "PluginOperationalStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AttentionInvalidatedAt",
                table: "AgentSchedules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingAttentionCorrelationId",
                table: "AgentSchedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingAttentionReason",
                table: "AgentSchedules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingAttentionTriggerCategory",
                table: "AgentSchedules",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StaffingReplenishmentRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceResourceChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GapsJson = table.Column<string>(type: "jsonb", nullable: false),
                    OperationalImpact = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    InterimControlsJson = table.Column<string>(type: "jsonb", nullable: false),
                    DecisionFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DecisionComment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecisionIdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    DecidedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffingReplenishmentRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StaffingReplenishmentRequests_CoreConversations_Conversatio~",
                        column: x => x.ConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffingReplenishmentRequests_OrganizationTeams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "OrganizationTeams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StaffingReplenishmentRequests_ResourceChangeRequests_Source~",
                        column: x => x.SourceResourceChangeRequestId,
                        principalTable: "ResourceChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingReplenishmentRequests_ConversationId",
                table: "StaffingReplenishmentRequests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_StaffingReplenishmentRequests_OrganizationId_ManagerOrganiz~",
                table: "StaffingReplenishmentRequests",
                columns: new[] { "OrganizationId", "ManagerOrganizationUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingReplenishmentRequests_OrganizationId_RequesterInsta~",
                table: "StaffingReplenishmentRequests",
                columns: new[] { "OrganizationId", "RequesterInstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffingReplenishmentRequests_SourceResourceChangeRequestId~",
                table: "StaffingReplenishmentRequests",
                columns: new[] { "SourceResourceChangeRequestId", "DecisionFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffingReplenishmentRequests_TeamId",
                table: "StaffingReplenishmentRequests",
                column: "TeamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffingReplenishmentRequests");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "PluginOperationalStates");

            migrationBuilder.DropColumn(
                name: "AttentionInvalidatedAt",
                table: "AgentSchedules");

            migrationBuilder.DropColumn(
                name: "PendingAttentionCorrelationId",
                table: "AgentSchedules");

            migrationBuilder.DropColumn(
                name: "PendingAttentionReason",
                table: "AgentSchedules");

            migrationBuilder.DropColumn(
                name: "PendingAttentionTriggerCategory",
                table: "AgentSchedules");
        }
    }
}
