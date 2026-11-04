using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessImportCallbackOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GitHubAuthorizationRequestedByApplicationUserId",
                table: "RepositoryOnboardingDrafts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingDrafts_OrganizationId_UpdatedAt",
                table: "RepositoryOnboardingDrafts",
                columns: new[] { "OrganizationId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedProjectProfiles_AnalysisDraftId_Status",
                table: "ImportedProjectProfiles",
                columns: new[] { "AnalysisDraftId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositoryOnboardingDrafts_OrganizationId_UpdatedAt",
                table: "RepositoryOnboardingDrafts");

            migrationBuilder.DropIndex(
                name: "IX_ImportedProjectProfiles_AnalysisDraftId_Status",
                table: "ImportedProjectProfiles");

            migrationBuilder.DropColumn(
                name: "GitHubAuthorizationRequestedByApplicationUserId",
                table: "RepositoryOnboardingDrafts");
        }
    }
}
