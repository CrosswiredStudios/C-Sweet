using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessFirstProjectImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositoryOnboardingSources_RepositoryId",
                table: "RepositoryOnboardingSources");

            migrationBuilder.AddColumn<bool>(
                name: "AssignAsProjectManager",
                table: "WorkforcePlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "AnalysisRequestedByApplicationUserId",
                table: "RepositoryOnboardingDrafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                table: "RepositoryOnboardingDrafts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartMode",
                table: "BusinessOnboardingOperations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.CreateTable(
                name: "BusinessSuggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    DismissedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessSuggestions_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportedProjectProfiles",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InferredJson = table.Column<string>(type: "text", nullable: false),
                    AcceptedJson = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnalysisRevision = table.Column<long>(type: "bigint", nullable: false),
                    AnalysisDraftId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedProjectProfiles", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ImportedProjectProfiles_Workstreams_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectRepositoryAssociations",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRepositoryAssociations", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_ProjectRepositoryAssociations_SourceControlRepositories_Rep~",
                        column: x => x.RepositoryId,
                        principalTable: "SourceControlRepositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectRepositoryAssociations_Workstreams_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingSources_RepositoryId",
                table: "RepositoryOnboardingSources",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessSuggestions_OrganizationId_Key",
                table: "BusinessSuggestions",
                columns: new[] { "OrganizationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRepositoryAssociations_ImportSourceId",
                table: "ProjectRepositoryAssociations",
                column: "ImportSourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRepositoryAssociations_OrganizationId_RepositoryId",
                table: "ProjectRepositoryAssociations",
                columns: new[] { "OrganizationId", "RepositoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRepositoryAssociations_RepositoryId",
                table: "ProjectRepositoryAssociations",
                column: "RepositoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessSuggestions");

            migrationBuilder.DropTable(
                name: "ImportedProjectProfiles");

            migrationBuilder.DropTable(
                name: "ProjectRepositoryAssociations");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryOnboardingSources_RepositoryId",
                table: "RepositoryOnboardingSources");

            migrationBuilder.DropColumn(
                name: "AssignAsProjectManager",
                table: "WorkforcePlans");

            migrationBuilder.DropColumn(
                name: "AnalysisRequestedByApplicationUserId",
                table: "RepositoryOnboardingDrafts");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "RepositoryOnboardingDrafts");

            migrationBuilder.DropColumn(
                name: "StartMode",
                table: "BusinessOnboardingOperations");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingSources_RepositoryId",
                table: "RepositoryOnboardingSources",
                column: "RepositoryId",
                unique: true);
        }
    }
}
