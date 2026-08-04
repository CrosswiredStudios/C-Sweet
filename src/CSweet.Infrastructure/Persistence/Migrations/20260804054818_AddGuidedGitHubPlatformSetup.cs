using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidedGitHubPlatformSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformGitHubAppCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerLogin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    AppName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AppSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InstallUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    ProtectionVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformGitHubAppCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformSourceControlSetupSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStep = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    GitHubOrganization = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PublicBaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ManifestCallbackUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PrerequisitesConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    SourceAccessPermissionsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    SourceAccessAppConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ProvisionerRequested = table.Column<bool>(type: "boolean", nullable: true),
                    ProvisionerPermissionsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ProvisionerAppConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ActivationConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PendingAppKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StateNonceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StateExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceAccessCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProvisionerCredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSourceControlSetupSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformGitHubAppCredentials_Kind",
                table: "PlatformGitHubAppCredentials",
                column: "Kind",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformGitHubAppCredentials_Kind_Status_UpdatedAt",
                table: "PlatformGitHubAppCredentials",
                columns: new[] { "Kind", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSourceControlSetupSessions_StartedByApplicationUser~",
                table: "PlatformSourceControlSetupSessions",
                columns: new[] { "StartedByApplicationUserId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformSourceControlSetupSessions_StateNonceHash_StateExpi~",
                table: "PlatformSourceControlSetupSessions",
                columns: new[] { "StateNonceHash", "StateExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformGitHubAppCredentials");

            migrationBuilder.DropTable(
                name: "PlatformSourceControlSetupSessions");
        }
    }
}
