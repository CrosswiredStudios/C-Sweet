using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChiefHiringAutonomy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedCatalogAgentJson",
                table: "WorkforcePlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectionRationale",
                table: "WorkforcePlans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DelegatedInstallationId",
                table: "StaffingActionProposals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChiefHiringPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    SetupStage = table.Column<string>(type: "text", nullable: false),
                    SetupComplete = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rationale = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefHiringPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChiefHiringPolicyRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefHiringPolicyRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HiringPlanDelegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiringPlanDelegations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChiefHiringPolicies_OrganizationId_InstallationId",
                table: "ChiefHiringPolicies",
                columns: new[] { "OrganizationId", "InstallationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChiefHiringPolicyRevisions_OrganizationId_InstallationId_Re~",
                table: "ChiefHiringPolicyRevisions",
                columns: new[] { "OrganizationId", "InstallationId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HiringPlanDelegations_ResourceChangeRequestId_InstallationId",
                table: "HiringPlanDelegations",
                columns: new[] { "ResourceChangeRequestId", "InstallationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChiefHiringPolicies");

            migrationBuilder.DropTable(
                name: "ChiefHiringPolicyRevisions");

            migrationBuilder.DropTable(
                name: "HiringPlanDelegations");

            migrationBuilder.DropColumn(
                name: "SelectedCatalogAgentJson",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "SelectionRationale",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "DelegatedInstallationId",
                table: "StaffingActionProposals");
        }
    }
}
