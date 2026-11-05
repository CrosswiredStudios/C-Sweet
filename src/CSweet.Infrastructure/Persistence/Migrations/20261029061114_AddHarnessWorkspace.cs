using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHarnessWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentHarnessPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    PolicyJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentHarnessPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentHarnessPolicies_AgentInstallations_Id",
                        column: x => x.Id,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessSkillRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessSkillRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HarnessTaskBudgets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    CostLimit = table.Column<decimal>(type: "numeric(24,10)", precision: 24, scale: 10, nullable: true),
                    HasUnpricedUsage = table.Column<bool>(type: "boolean", nullable: false),
                    ChargedTokens = table.Column<long>(type: "bigint", nullable: false),
                    ChargedCost = table.Column<decimal>(type: "numeric(24,10)", precision: 24, scale: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarnessTaskBudgets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HarnessBudgetReservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tokens = table.Column<long>(type: "bigint", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(24,10)", precision: 24, scale: 10, nullable: false),
                    Settled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HarnessBudgetReservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HarnessBudgetReservations_HarnessTaskBudgets_BudgetId",
                        column: x => x.BudgetId,
                        principalTable: "HarnessTaskBudgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSkillRevisions_BusinessId_Name_Version",
                table: "BusinessSkillRevisions",
                columns: new[] { "BusinessId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HarnessBudgetReservations_BudgetId",
                table: "HarnessBudgetReservations",
                column: "BudgetId");

            migrationBuilder.CreateIndex(
                name: "IX_HarnessBudgetReservations_RunId",
                table: "HarnessBudgetReservations",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentHarnessPolicies");

            migrationBuilder.DropTable(
                name: "BusinessSkillRevisions");

            migrationBuilder.DropTable(
                name: "HarnessBudgetReservations");

            migrationBuilder.DropTable(
                name: "HarnessTaskBudgets");
        }
    }
}
