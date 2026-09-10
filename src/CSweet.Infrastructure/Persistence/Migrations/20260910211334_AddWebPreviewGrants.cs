using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebPreviewGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebPreviewGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApprovalProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalContentDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ReservedCpuSeconds = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_ActionProposals_ApprovalProposalId",
                        column: x => x.ApprovalProposalId,
                        principalTable: "ActionProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_AgentInstallations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_AgentInstallations_ProviderInstallationId",
                        column: x => x.ProviderInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_ArtifactRevisions_ApprovalRevisionId",
                        column: x => x.ApprovalRevisionId,
                        principalTable: "ArtifactRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_CoreArtifacts_ApprovalArtifactId",
                        column: x => x.ApprovalArtifactId,
                        principalTable: "CoreArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebPreviewGrants_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    WebHostId = table.Column<Guid>(type: "uuid", nullable: true),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    ManifestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccessReference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPreviewJobs_WebPreviewGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "WebPreviewGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_ApprovalArtifactId",
                table: "WebPreviewGrants",
                column: "ApprovalArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_ApprovalProposalId",
                table: "WebPreviewGrants",
                column: "ApprovalProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_ApprovalRevisionId",
                table: "WebPreviewGrants",
                column: "ApprovalRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_InstallationId",
                table: "WebPreviewGrants",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_OrganizationId_InstallationId_IdempotencyK~",
                table: "WebPreviewGrants",
                columns: new[] { "OrganizationId", "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_OrganizationId_WorkstreamId_Status",
                table: "WebPreviewGrants",
                columns: new[] { "OrganizationId", "WorkstreamId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_ProviderInstallationId",
                table: "WebPreviewGrants",
                column: "ProviderInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewGrants_WorkstreamId",
                table: "WebPreviewGrants",
                column: "WorkstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewJobs_GrantId",
                table: "WebPreviewJobs",
                column: "GrantId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewJobs_OrganizationId_InstallationId_IdempotencyKey",
                table: "WebPreviewJobs",
                columns: new[] { "OrganizationId", "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewJobs_OrganizationId_WorkstreamId_Phase_ExpiresAt",
                table: "WebPreviewJobs",
                columns: new[] { "OrganizationId", "WorkstreamId", "Phase", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebPreviewJobs");

            migrationBuilder.DropTable(
                name: "WebPreviewGrants");
        }
    }
}
