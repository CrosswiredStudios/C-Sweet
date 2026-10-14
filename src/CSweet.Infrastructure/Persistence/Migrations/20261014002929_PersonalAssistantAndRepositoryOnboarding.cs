using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonalAssistantAndRepositoryOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PersonalAssistantModel",
                table: "SystemConfigurations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PersonalAssistantProviderId",
                table: "SystemConfigurations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipientApplicationUserId",
                table: "ApplicationRealtimeOutbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsBundled",
                table: "AgentPackageSources",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerApplicationUserId",
                table: "AgentInstallations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PersonalAssistantMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalAssistantMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersonalAssistantProfiles",
                columns: table => new
                {
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalAssistantProfiles", x => x.ApplicationUserId);
                    table.ForeignKey(
                        name: "FK_PersonalAssistantProfiles_AgentInstallations_AgentInstallat~",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryOnboardingDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReviewJson = table.Column<string>(type: "text", nullable: false),
                    AnalysisJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    BriefArtifactId = table.Column<Guid>(type: "uuid", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GitHubStateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GitHubStateExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerifiedInstallationsJson = table.Column<string>(type: "text", nullable: false),
                    ActionUri = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryOnboardingDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryOnboardingSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    InstallationId = table.Column<long>(type: "bigint", nullable: true),
                    ExternalRepositoryId = table.Column<long>(type: "bigint", nullable: true),
                    Owner = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DefaultBranch = table.Column<string>(type: "text", nullable: false),
                    IsPrivate = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ArchiveKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AnalysisInputJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attached = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryOnboardingSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryOnboardingSources_RepositoryOnboardingDrafts_Draf~",
                        column: x => x.DraftId,
                        principalTable: "RepositoryOnboardingDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstallations_OwnerApplicationUserId",
                table: "AgentInstallations",
                column: "OwnerApplicationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAssistantMessages_ApplicationUserId_DraftId_Created~",
                table: "PersonalAssistantMessages",
                columns: new[] { "ApplicationUserId", "DraftId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAssistantMessages_ApplicationUserId_IdempotencyKey",
                table: "PersonalAssistantMessages",
                columns: new[] { "ApplicationUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonalAssistantProfiles_AgentInstallationId",
                table: "PersonalAssistantProfiles",
                column: "AgentInstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingDrafts_ApplicationUserId_UpdatedAt",
                table: "RepositoryOnboardingDrafts",
                columns: new[] { "ApplicationUserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingDrafts_Status_LeaseUntil",
                table: "RepositoryOnboardingDrafts",
                columns: new[] { "Status", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingSources_DraftId_Kind_InstallationId_Ext~",
                table: "RepositoryOnboardingSources",
                columns: new[] { "DraftId", "Kind", "InstallationId", "ExternalRepositoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryOnboardingSources_RepositoryId",
                table: "RepositoryOnboardingSources",
                column: "RepositoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonalAssistantMessages");

            migrationBuilder.DropTable(
                name: "PersonalAssistantProfiles");

            migrationBuilder.DropTable(
                name: "RepositoryOnboardingSources");

            migrationBuilder.DropTable(
                name: "RepositoryOnboardingDrafts");

            migrationBuilder.DropIndex(
                name: "IX_AgentInstallations_OwnerApplicationUserId",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "PersonalAssistantModel",
                table: "SystemConfigurations");

            migrationBuilder.DropColumn(
                name: "PersonalAssistantProviderId",
                table: "SystemConfigurations");

            migrationBuilder.DropColumn(
                name: "RecipientApplicationUserId",
                table: "ApplicationRealtimeOutbox");

            migrationBuilder.DropColumn(
                name: "IsBundled",
                table: "AgentPackageSources");

            migrationBuilder.DropColumn(
                name: "OwnerApplicationUserId",
                table: "AgentInstallations");
        }
    }
}
