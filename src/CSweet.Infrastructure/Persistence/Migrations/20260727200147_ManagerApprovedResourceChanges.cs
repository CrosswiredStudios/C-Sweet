using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManagerApprovedResourceChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Headcount",
                table: "WorkforcePlans",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RoleKey",
                table: "WorkforcePlans",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceResourceChangeRequestId",
                table: "WorkforcePlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetInstallationId",
                table: "AgentPlatformEventOutbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ResourceChangeRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatTurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupersedesRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductGoal = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ContextRevision = table.Column<long>(type: "bigint", nullable: false),
                    AssumptionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConstraintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeliveryStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DecisionComment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecidedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionIdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceChangeRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceChangeRequests_CoreConversationMessages_Conversatio~",
                        column: x => x.ConversationMessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ResourceChangeRequests_CoreConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResourceChangeRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Team = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Headcount = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Timing = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    HumanRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ReportsToOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportsToRoleKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ChangeKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IsDesired = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousRoleJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceChangeRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceChangeRoles_ResourceChangeRequests_ResourceChangeRe~",
                        column: x => x.ResourceChangeRequestId,
                        principalTable: "ResourceChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkforcePlans_OrganizationId_RequestingInstallationId_Role~",
                table: "WorkforcePlans",
                columns: new[] { "OrganizationId", "RequestingInstallationId", "RoleKey" },
                filter: "\"RoleKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_ConversationId",
                table: "ResourceChangeRequests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_ConversationMessageId",
                table: "ResourceChangeRequests",
                column: "ConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_OrganizationId_ManagerOrganizationUs~",
                table: "ResourceChangeRequests",
                columns: new[] { "OrganizationId", "ManagerOrganizationUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_OrganizationId_RequesterInstallation~",
                table: "ResourceChangeRequests",
                columns: new[] { "OrganizationId", "RequesterInstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRoles_ResourceChangeRequestId_RoleKey",
                table: "ResourceChangeRoles",
                columns: new[] { "ResourceChangeRequestId", "RoleKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceChangeRoles");

            migrationBuilder.DropTable(
                name: "ResourceChangeRequests");

            migrationBuilder.DropIndex(
                name: "IX_WorkforcePlans_OrganizationId_RequestingInstallationId_Role~",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "Headcount",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "RoleKey",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "SourceResourceChangeRequestId",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "TargetInstallationId",
                table: "AgentPlatformEventOutbox");
        }
    }
}
