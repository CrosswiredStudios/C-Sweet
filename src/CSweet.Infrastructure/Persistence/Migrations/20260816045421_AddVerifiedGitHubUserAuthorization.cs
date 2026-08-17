using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedGitHubUserAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceControlConnections_Provider_ProviderAccountId",
                table: "SourceControlConnections");

            migrationBuilder.AddColumn<string>(
                name: "ProviderRepositoryKey",
                table: "SourceControlRepositories",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "PlatformGitHubAppCredentials",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProtectedClientSecret",
                table: "PlatformGitHubAppCredentials",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "SourceControlRepositories" AS repository
                SET "ProviderRepositoryKey" = CASE
                    WHEN connection."Provider" = 'GitHub'
                        THEN 'github:' || repository."ExternalRepositoryId"
                    ELSE 'generic:' || repository."OrganizationId"::text || ':' || repository."ExternalRepositoryId"
                END
                FROM "SourceControlConnections" AS connection
                WHERE connection."Id" = repository."ConnectionId"
                  AND connection."OrganizationId" = repository."OrganizationId";
                """);

            // Pre-release reset: earlier GitHub App records do not contain the one-time OAuth
            // client secret and earlier business connections were accepted without proving that
            // the current GitHub user could access the installation. They must be configured and
            // connected again through the verified flow.
            migrationBuilder.Sql("DELETE FROM \"PlatformSourceControlSetupSessions\";");
            migrationBuilder.Sql("DELETE FROM \"PlatformGitHubAppCredentials\";");
            migrationBuilder.Sql("""
                UPDATE "SourceControlOnboardingSessions"
                SET "Status" = 'Cancelled',
                    "StateNonceHash" = '',
                    "CompletedAt" = NOW(),
                    "UpdatedAt" = NOW(),
                    "Revision" = "Revision" + 1
                WHERE "Status" <> 'Completed' AND "Status" <> 'Cancelled';
                """);
            migrationBuilder.Sql("""
                UPDATE "SourceControlConnections"
                SET "Status" = 'Disconnected',
                    "SourceAccessInstallationId" = NULL,
                    "ProvisionerInstallationId" = NULL,
                    "LastHealthError" = 'Reconnect GitHub to verify the signed-in account.',
                    "UpdatedAt" = NOW(),
                    "Revision" = "Revision" + 1
                WHERE "Provider" = 'GitHub';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlRepositories_ProviderRepositoryKey",
                table: "SourceControlRepositories",
                column: "ProviderRepositoryKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceControlRepositories_ProviderRepositoryKey",
                table: "SourceControlRepositories");

            migrationBuilder.DropColumn(
                name: "ProviderRepositoryKey",
                table: "SourceControlRepositories");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "PlatformGitHubAppCredentials");

            migrationBuilder.DropColumn(
                name: "ProtectedClientSecret",
                table: "PlatformGitHubAppCredentials");

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlConnections_Provider_ProviderAccountId",
                table: "SourceControlConnections",
                columns: new[] { "Provider", "ProviderAccountId" },
                unique: true,
                filter: "\"Provider\" = 'GitHub'");
        }
    }
}
