using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecureCollaborativeDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_OrganizationId",
                table: "CoreArtifacts");

            migrationBuilder.AddColumn<Guid>(
                name: "AcceptedRevisionId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "CoreArtifacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByOrganizationUserId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatorAgentId",
                table: "CoreArtifacts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatorAgentVersion",
                table: "CoreArtifacts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatorDisplayName",
                table: "CoreArtifacts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentStatus",
                table: "CoreArtifacts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentType",
                table: "CoreArtifacts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "FolderId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LatestRevisionId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginConversationId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginWorkItemId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PackageId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StewardOrganizationUserId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmittedRevisionId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArtifactRevisionId",
                table: "CoreApprovals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DecidedByAgentInstallationId",
                table: "CoreApprovals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DecidedByOrganizationUserId",
                table: "CoreApprovals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceConversationMessageId",
                table: "CoreApprovals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArtifactAccessRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestingInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Justification = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceConversationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactAccessRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtifactAccessRequests_CoreArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "CoreArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtifactFolders_ArtifactFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "ArtifactFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PackageType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcceptedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactPackages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactReviewJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactReviewJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    BaseRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Content = table.Column<string>(type: "character varying(131072)", maxLength: 131072, nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByAgentInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatorDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtifactRevisions_CoreArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "CoreArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessageArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessageArtifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMessageArtifacts_CoreArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "CoreArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMessageArtifacts_CoreConversationMessages_Messa~",
                        column: x => x.MessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactPackageMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    RequiredDocumentType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactPackageMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtifactPackageMembers_ArtifactPackages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "ArtifactPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtifactPackageMembers_CoreArtifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "CoreArtifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve every legacy artifact as revision 1. The deterministic UUID and
            // idempotency key make the data move safe to retry during deployment.
            migrationBuilder.Sql("""
                INSERT INTO "ArtifactRevisions" (
                    "Id", "OrganizationId", "ArtifactId", "Number", "BaseRevisionId",
                    "Content", "ContentSha256", "Status", "CreatedByOrganizationUserId",
                    "CreatedByAgentInstallationId", "CreatorDisplayName", "IdempotencyKey",
                    "CreatedAt", "SubmittedAt", "DecidedAt")
                SELECT
                    md5(a."Id"::text || ':revision:1')::uuid,
                    a."OrganizationId", a."Id", 1, NULL, a."Content",
                    md5(a."Content") || md5(a."Content" || ':legacy'),
                    CASE WHEN a."ApprovalStatus" = 'Approved' THEN 'Accepted' ELSE 'Draft' END,
                    NULL, NULL, 'Legacy artifact', 'legacy:' || a."Id"::text,
                    a."CreatedAt", NULL,
                    CASE WHEN a."ApprovalStatus" = 'Approved' THEN a."UpdatedAt" ELSE NULL END
                FROM "CoreArtifacts" a;

                UPDATE "CoreArtifacts" a SET
                    "LatestRevisionId" = md5(a."Id"::text || ':revision:1')::uuid,
                    "AcceptedRevisionId" = CASE WHEN a."ApprovalStatus" = 'Approved'
                        THEN md5(a."Id"::text || ':revision:1')::uuid ELSE NULL END,
                    "CreatorDisplayName" = 'Legacy artifact',
                    "DocumentType" = lower(a."Type"),
                    "DocumentStatus" = CASE
                        WHEN a."ApprovalStatus" = 'Approved' THEN 'Approved'
                        WHEN a."ApprovalStatus" IN ('Rejected', 'RevisionRequested') THEN 'ChangesRequested'
                        ELSE 'Draft' END;

                UPDATE "CoreApprovals" p SET "ArtifactRevisionId" =
                    md5(p."ArtifactId"::text || ':revision:1')::uuid;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_CreatedByOrganizationUserId",
                table: "CoreArtifacts",
                column: "CreatedByOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_FolderId",
                table: "CoreArtifacts",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_OrganizationId_ArchivedAt_UpdatedAt",
                table: "CoreArtifacts",
                columns: new[] { "OrganizationId", "ArchivedAt", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_OrganizationId_FolderId",
                table: "CoreArtifacts",
                columns: new[] { "OrganizationId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_OrganizationId_PackageId",
                table: "CoreArtifacts",
                columns: new[] { "OrganizationId", "PackageId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_PackageId",
                table: "CoreArtifacts",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_StewardOrganizationUserId",
                table: "CoreArtifacts",
                column: "StewardOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreApprovals_ArtifactRevisionId",
                table: "CoreApprovals",
                column: "ArtifactRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactAccessRequests_ArtifactId_SubjectKind_SubjectId_Sta~",
                table: "ArtifactAccessRequests",
                columns: new[] { "ArtifactId", "SubjectKind", "SubjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactAccessRequests_OrganizationId_IdempotencyKey",
                table: "ArtifactAccessRequests",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactFolders_OrganizationId_ParentFolderId_Name",
                table: "ArtifactFolders",
                columns: new[] { "OrganizationId", "ParentFolderId", "Name" },
                unique: true,
                filter: "\"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactFolders_ParentFolderId",
                table: "ArtifactFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactPackageMembers_ArtifactId",
                table: "ArtifactPackageMembers",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactPackageMembers_PackageId_ArtifactId",
                table: "ArtifactPackageMembers",
                columns: new[] { "PackageId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactPackageMembers_PackageId_Position",
                table: "ArtifactPackageMembers",
                columns: new[] { "PackageId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactPackages_OrganizationId_ArchivedAt_UpdatedAt",
                table: "ArtifactPackages",
                columns: new[] { "OrganizationId", "ArchivedAt", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactReviewJobs_OrganizationId_IdempotencyKey",
                table: "ArtifactReviewJobs",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactReviewJobs_Status_NextAttemptAt_CreatedAt",
                table: "ArtifactReviewJobs",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRevisions_ArtifactId_Number",
                table: "ArtifactRevisions",
                columns: new[] { "ArtifactId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactRevisions_OrganizationId_IdempotencyKey",
                table: "ArtifactRevisions",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageArtifacts_ArtifactId",
                table: "ConversationMessageArtifacts",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageArtifacts_MessageId_ArtifactId",
                table: "ConversationMessageArtifacts",
                columns: new[] { "MessageId", "ArtifactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageArtifacts_OrganizationId_ConversationId",
                table: "ConversationMessageArtifacts",
                columns: new[] { "OrganizationId", "ConversationId" });

            migrationBuilder.AddForeignKey(
                name: "FK_CoreApprovals_ArtifactRevisions_ArtifactRevisionId",
                table: "CoreApprovals",
                column: "ArtifactRevisionId",
                principalTable: "ArtifactRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreArtifacts_ArtifactFolders_FolderId",
                table: "CoreArtifacts",
                column: "FolderId",
                principalTable: "ArtifactFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreArtifacts_ArtifactPackages_PackageId",
                table: "CoreArtifacts",
                column: "PackageId",
                principalTable: "ArtifactPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreArtifacts_CoreOrganizationUsers_CreatedByOrganizationUs~",
                table: "CoreArtifacts",
                column: "CreatedByOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreArtifacts_CoreOrganizationUsers_StewardOrganizationUser~",
                table: "CoreArtifacts",
                column: "StewardOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreApprovals_ArtifactRevisions_ArtifactRevisionId",
                table: "CoreApprovals");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreArtifacts_ArtifactFolders_FolderId",
                table: "CoreArtifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreArtifacts_ArtifactPackages_PackageId",
                table: "CoreArtifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreArtifacts_CoreOrganizationUsers_CreatedByOrganizationUs~",
                table: "CoreArtifacts");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreArtifacts_CoreOrganizationUsers_StewardOrganizationUser~",
                table: "CoreArtifacts");

            migrationBuilder.DropTable(
                name: "ArtifactAccessRequests");

            migrationBuilder.DropTable(
                name: "ArtifactFolders");

            migrationBuilder.DropTable(
                name: "ArtifactPackageMembers");

            migrationBuilder.DropTable(
                name: "ArtifactReviewJobs");

            migrationBuilder.DropTable(
                name: "ArtifactRevisions");

            migrationBuilder.DropTable(
                name: "ConversationMessageArtifacts");

            migrationBuilder.DropTable(
                name: "ArtifactPackages");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_CreatedByOrganizationUserId",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_FolderId",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_OrganizationId_ArchivedAt_UpdatedAt",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_OrganizationId_FolderId",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_OrganizationId_PackageId",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_PackageId",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_StewardOrganizationUserId",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_CoreApprovals_ArtifactRevisionId",
                table: "CoreApprovals");

            migrationBuilder.DropColumn(
                name: "AcceptedRevisionId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "CreatedByOrganizationUserId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "CreatorAgentId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "CreatorAgentVersion",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "CreatorDisplayName",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "DocumentStatus",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "DocumentType",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "LatestRevisionId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "OriginConversationId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "OriginWorkItemId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "StewardOrganizationUserId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "SubmittedRevisionId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "ArtifactRevisionId",
                table: "CoreApprovals");

            migrationBuilder.DropColumn(
                name: "DecidedByAgentInstallationId",
                table: "CoreApprovals");

            migrationBuilder.DropColumn(
                name: "DecidedByOrganizationUserId",
                table: "CoreApprovals");

            migrationBuilder.DropColumn(
                name: "EvidenceConversationMessageId",
                table: "CoreApprovals");

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_OrganizationId",
                table: "CoreArtifacts",
                column: "OrganizationId");
        }
    }
}
