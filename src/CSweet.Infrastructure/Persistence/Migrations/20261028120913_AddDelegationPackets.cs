using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDelegationPackets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Delegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FromInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    InputArtifactIdsJson = table.Column<string>(type: "text", nullable: false),
                    ReturnContract = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedByInstallationId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Delegations_AgentInstallations_FromInstallationId",
                        column: x => x.FromInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Delegations_AgentInstallations_ToInstallationId",
                        column: x => x.ToInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_BusinessId",
                table: "Delegations",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_BusinessId_Status_UpdatedAt",
                table: "Delegations",
                columns: new[] { "BusinessId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_FromInstallationId",
                table: "Delegations",
                column: "FromInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_Status",
                table: "Delegations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_ToInstallationId_Status",
                table: "Delegations",
                columns: new[] { "ToInstallationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Delegations");
        }
    }
}
