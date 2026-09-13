using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteAutomaticComputeSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComputeLocalSetups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    HandoffHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HandoffExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TemplateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeLocalSetups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeLocalSetups_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComputeAgentAccess",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SetupId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantsCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeAgentAccess", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeAgentAccess_AgentInstallations_Id",
                        column: x => x.Id,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComputeAgentAccess_ComputeLocalSetups_SetupId",
                        column: x => x.SetupId,
                        principalTable: "ComputeLocalSetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComputeAgentAccess_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeAgentAccess_SetupId",
                table: "ComputeAgentAccess",
                column: "SetupId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeAgentAccess_WorkstreamId",
                table: "ComputeAgentAccess",
                column: "WorkstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeLocalSetups_OrganizationId",
                table: "ComputeLocalSetups",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputeAgentAccess");

            migrationBuilder.DropTable(
                name: "ComputeLocalSetups");
        }
    }
}
