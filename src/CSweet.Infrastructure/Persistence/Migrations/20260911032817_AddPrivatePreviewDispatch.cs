using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivatePreviewDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ArtifactLength",
                table: "WebPreviewJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "AssignmentJson",
                table: "WebPreviewJobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "LastEvidenceSequence",
                table: "WebPreviewJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SourceArtifactDigest",
                table: "WebPreviewJobs",
                type: "character varying(71)",
                maxLength: 71,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TeardownConfirmedAt",
                table: "WebPreviewJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WebHostCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WebHostId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrowserSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BodyJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResponseJson = table.Column<string>(type: "text", nullable: true),
                    ResponseDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebHostCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebHostCommands_WebHostRegistrations_WebHostId",
                        column: x => x.WebHostId,
                        principalTable: "WebHostRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebHostCommands_WebPreviewJobs_PreviewId",
                        column: x => x.PreviewId,
                        principalTable: "WebPreviewJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewBrowserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    SessionHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    TicketExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TicketConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientEventCount = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewBrowserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPreviewBrowserSessions_WebPreviewJobs_PreviewId",
                        column: x => x.PreviewId,
                        principalTable: "WebPreviewJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostSequence = table.Column<long>(type: "bigint", nullable: false),
                    DiagnosticJson = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPreviewEvidence_WebPreviewJobs_PreviewId",
                        column: x => x.PreviewId,
                        principalTable: "WebPreviewJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TriageWorkId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriageInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebPreviewFindings_WebPreviewJobs_PreviewId",
                        column: x => x.PreviewId,
                        principalTable: "WebPreviewJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewProjectAdmissions",
                columns: table => new
                {
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewProjectAdmissions", x => x.WorkstreamId);
                    table.ForeignKey(
                        name: "FK_WebPreviewProjectAdmissions_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewTriageRoutes",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPreviewTriageRoutes", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_WebPreviewTriageRoutes_Workstreams_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebHostCommands_PreviewId",
                table: "WebHostCommands",
                column: "PreviewId");

            migrationBuilder.CreateIndex(
                name: "IX_WebHostCommands_WebHostId_Status_CreatedAt",
                table: "WebHostCommands",
                columns: new[] { "WebHostId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewBrowserSessions_ExpiresAt",
                table: "WebPreviewBrowserSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewBrowserSessions_PreviewId",
                table: "WebPreviewBrowserSessions",
                column: "PreviewId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewBrowserSessions_SessionHash",
                table: "WebPreviewBrowserSessions",
                column: "SessionHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewBrowserSessions_TicketHash",
                table: "WebPreviewBrowserSessions",
                column: "TicketHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewEvidence_PreviewId_HostSequence",
                table: "WebPreviewEvidence",
                columns: new[] { "PreviewId", "HostSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewEvidence_RetainUntil",
                table: "WebPreviewEvidence",
                column: "RetainUntil");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewFindings_OrganizationId_ProjectId_BuildId_Fingerp~",
                table: "WebPreviewFindings",
                columns: new[] { "OrganizationId", "ProjectId", "BuildId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewFindings_PreviewId",
                table: "WebPreviewFindings",
                column: "PreviewId");

            migrationBuilder.CreateIndex(
                name: "IX_WebPreviewFindings_RetainUntil",
                table: "WebPreviewFindings",
                column: "RetainUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebHostCommands");

            migrationBuilder.DropTable(
                name: "WebPreviewBrowserSessions");

            migrationBuilder.DropTable(
                name: "WebPreviewEvidence");

            migrationBuilder.DropTable(
                name: "WebPreviewFindings");

            migrationBuilder.DropTable(
                name: "WebPreviewProjectAdmissions");

            migrationBuilder.DropTable(
                name: "WebPreviewTriageRoutes");

            migrationBuilder.DropColumn(
                name: "ArtifactLength",
                table: "WebPreviewJobs");

            migrationBuilder.DropColumn(
                name: "AssignmentJson",
                table: "WebPreviewJobs");

            migrationBuilder.DropColumn(
                name: "LastEvidenceSequence",
                table: "WebPreviewJobs");

            migrationBuilder.DropColumn(
                name: "SourceArtifactDigest",
                table: "WebPreviewJobs");

            migrationBuilder.DropColumn(
                name: "TeardownConfirmedAt",
                table: "WebPreviewJobs");
        }
    }
}
