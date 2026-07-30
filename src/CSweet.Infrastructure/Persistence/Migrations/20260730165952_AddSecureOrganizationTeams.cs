using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureOrganizationTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "WorkforcePlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "WorkBoards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "ResourceChangeRoles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamDescription",
                table: "ResourceChangeRequests",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "ResourceChangeRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamKey",
                table: "ResourceChangeRequests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamName",
                table: "ResourceChangeRequests",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrganizationTeams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LeadOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationTeams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationTeams_CoreOrganizationUsers_LeadOrganizationUse~",
                        column: x => x.LeadOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizationTeams_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamRoleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExclusiveAgentEmployeeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_CoreOrganizationUsers_OrganizationUserId",
                        column: x => x.OrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_CoreRoles_TeamRoleId",
                        column: x => x.TeamRoleId,
                        principalTable: "CoreRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_OrganizationTeams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "OrganizationTeams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkforcePlans_TeamId",
                table: "WorkforcePlans",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_TeamId",
                table: "WorkBoards",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRoles_TeamId",
                table: "ResourceChangeRoles",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceChangeRequests_TeamId",
                table: "ResourceChangeRequests",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationTeams_LeadOrganizationUserId",
                table: "OrganizationTeams",
                column: "LeadOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationTeams_OrganizationId_NormalizedName",
                table: "OrganizationTeams",
                columns: new[] { "OrganizationId", "NormalizedName" },
                unique: true,
                filter: "\"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationTeams_OrganizationId_TeamKey",
                table: "OrganizationTeams",
                columns: new[] { "OrganizationId", "TeamKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_ExclusiveAgentEmployeeId",
                table: "TeamMemberships",
                column: "ExclusiveAgentEmployeeId",
                unique: true,
                filter: "\"ExclusiveAgentEmployeeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_OrganizationId_OrganizationUserId_EndedAt",
                table: "TeamMemberships",
                columns: new[] { "OrganizationId", "OrganizationUserId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_OrganizationUserId",
                table: "TeamMemberships",
                column: "OrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_TeamId_OrganizationUserId",
                table: "TeamMemberships",
                columns: new[] { "TeamId", "OrganizationUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_TeamRoleId",
                table: "TeamMemberships",
                column: "TeamRoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChangeRequests_OrganizationTeams_TeamId",
                table: "ResourceChangeRequests",
                column: "TeamId",
                principalTable: "OrganizationTeams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ResourceChangeRoles_OrganizationTeams_TeamId",
                table: "ResourceChangeRoles",
                column: "TeamId",
                principalTable: "OrganizationTeams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkBoards_OrganizationTeams_TeamId",
                table: "WorkBoards",
                column: "TeamId",
                principalTable: "OrganizationTeams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkforcePlans_OrganizationTeams_TeamId",
                table: "WorkforcePlans",
                column: "TeamId",
                principalTable: "OrganizationTeams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChangeRequests_OrganizationTeams_TeamId",
                table: "ResourceChangeRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_ResourceChangeRoles_OrganizationTeams_TeamId",
                table: "ResourceChangeRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkBoards_OrganizationTeams_TeamId",
                table: "WorkBoards");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkforcePlans_OrganizationTeams_TeamId",
                table: "WorkforcePlans");

            migrationBuilder.DropTable(
                name: "TeamMemberships");

            migrationBuilder.DropTable(
                name: "OrganizationTeams");

            migrationBuilder.DropIndex(
                name: "IX_WorkforcePlans_TeamId",
                table: "WorkforcePlans");

            migrationBuilder.DropIndex(
                name: "IX_WorkBoards_TeamId",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_ResourceChangeRoles_TeamId",
                table: "ResourceChangeRoles");

            migrationBuilder.DropIndex(
                name: "IX_ResourceChangeRequests_TeamId",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "ResourceChangeRoles");

            migrationBuilder.DropColumn(
                name: "TeamDescription",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "TeamKey",
                table: "ResourceChangeRequests");

            migrationBuilder.DropColumn(
                name: "TeamName",
                table: "ResourceChangeRequests");
        }
    }
}
