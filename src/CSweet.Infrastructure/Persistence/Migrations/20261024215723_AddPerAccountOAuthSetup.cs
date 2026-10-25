using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerAccountOAuthSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActiveCredentialAttemptId",
                table: "PluginConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CredentialProfileHash",
                table: "PluginConnections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RefreshLeaseOwner",
                table: "PluginConnections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefreshLeaseUntil",
                table: "PluginConnections",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderProfileHash",
                table: "ConnectorProfileApprovals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PluginAccountOperators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountOperators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountOperators_AgentInstallations_PluginInstallatio~",
                        column: x => x.PluginInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountRevocationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProfileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CredentialKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttempt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountRevocationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountRevocationJobs_AgentInstallations_PluginInstal~",
                        column: x => x.PluginInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PluginAccountSetupSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PluginInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplacesConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacesAuthorizationRevision = table.Column<long>(type: "bigint", nullable: true),
                    PackageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProfileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ScopeSetIdsJson = table.Column<string>(type: "text", nullable: false),
                    RequestedScopesJson = table.Column<string>(type: "text", nullable: false),
                    StateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObservedResourcesJson = table.Column<string>(type: "text", nullable: false),
                    PageToken = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SeenPageTokensJson = table.Column<string>(type: "text", nullable: false),
                    VerifiedSubject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    VerifiedName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IdentityAuthority = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RetiredCredentialKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PagesImported = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PolicyAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginAccountSetupSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginAccountSetupSessions_AgentInstallations_PluginInstall~",
                        column: x => x.PluginInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountGroups_PluginInstallationId_ProviderProfile_Id~",
                table: "PluginAccountGroups",
                columns: new[] { "PluginInstallationId", "ProviderProfile", "IdentityAuthority", "VerifiedSubject" },
                unique: true,
                filter: "\"VerifiedSubject\" IS NOT NULL AND \"State\" = 'Connected'");

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountOperators_PluginInstallationId_ApplicationUser~",
                table: "PluginAccountOperators",
                columns: new[] { "PluginInstallationId", "ApplicationUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountRevocationJobs_ConnectionId",
                table: "PluginAccountRevocationJobs",
                column: "ConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountRevocationJobs_PluginInstallationId",
                table: "PluginAccountRevocationJobs",
                column: "PluginInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountRevocationJobs_Status_NextAttempt",
                table: "PluginAccountRevocationJobs",
                columns: new[] { "Status", "NextAttempt" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountSetupSessions_PluginInstallationId_Application~",
                table: "PluginAccountSetupSessions",
                columns: new[] { "PluginInstallationId", "ApplicationUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginAccountSetupSessions_Status_LeaseUntil",
                table: "PluginAccountSetupSessions",
                columns: new[] { "Status", "LeaseUntil" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginAccountOperators");

            migrationBuilder.DropTable(
                name: "PluginAccountRevocationJobs");

            migrationBuilder.DropTable(
                name: "PluginAccountSetupSessions");

            migrationBuilder.DropIndex(
                name: "IX_PluginAccountGroups_PluginInstallationId_ProviderProfile_Id~",
                table: "PluginAccountGroups");

            migrationBuilder.DropColumn(
                name: "ActiveCredentialAttemptId",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "CredentialProfileHash",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "RefreshLeaseOwner",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "RefreshLeaseUntil",
                table: "PluginConnections");

            migrationBuilder.DropColumn(
                name: "ProviderProfileHash",
                table: "ConnectorProfileApprovals");
        }
    }
}
