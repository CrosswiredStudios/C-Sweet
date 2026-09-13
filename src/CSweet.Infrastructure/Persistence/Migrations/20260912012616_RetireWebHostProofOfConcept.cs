using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireWebHostProofOfConcept : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Do not discard the only control-plane record of an unconfirmed physical workload.
            // Drain using the previous runtime and confirm teardown before deploying this migration.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "WebPreviewJobs" WHERE "TeardownConfirmedAt" IS NULL) THEN
                        RAISE EXCEPTION 'WebHost retirement requires confirmed teardown of every preview workload before schema removal.';
                    END IF;
                END $$;
                UPDATE "ActionProposals" SET "Status" = 'Cancelled', "DecidedAt" = CURRENT_TIMESTAMP
                    WHERE "ActionType" = 'web-preview.grant' AND "Status" = 'Pending';
                UPDATE "AgentPlatformEventOutbox" SET "Status" = 'Failed', "LastError" = 'WebHost proof of concept retired.'
                    WHERE "EventType" = 'com.csweet.web-preview.changed.v1' AND "Status" = 'Pending';
                """);

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

            migrationBuilder.DropTable(
                name: "WebHostRegistrations");

            migrationBuilder.DropTable(
                name: "WebPreviewJobs");

            migrationBuilder.DropTable(
                name: "WebPreviewGrants");
        }

        /// <inheritdoc />
        // Restores empty schema only; retired rows and cancelled authorities cannot be recovered.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebHostRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BootstrapJson = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdentityKeyDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    IdentityPublicKeyBase64 = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    MaximumCapacityJson = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RegisteredByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    RegistrationRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportedHeartbeatJson = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebHostRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebHostRegistrations_AgentInstallations_ProviderInstallatio~",
                        column: x => x.ProviderInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WebHostRegistrations_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebPreviewGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalContentDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    ApprovalProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyJson = table.Column<string>(type: "text", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservedCpuSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false)
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
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "WebPreviewJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccessReference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ArtifactLength = table.Column<long>(type: "bigint", nullable: false),
                    AssignmentJson = table.Column<string>(type: "text", nullable: false),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastAccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastEvidenceSequence = table.Column<long>(type: "bigint", nullable: false),
                    ManifestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SourceArtifactDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    TeardownConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WebHostId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "WebHostCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BodyJson = table.Column<string>(type: "text", nullable: false),
                    BrowserSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    ResponseJson = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WebHostId = table.Column<Guid>(type: "uuid", nullable: false)
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
                    ClientEventCount = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SessionHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    TicketConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TicketExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TicketHash = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false)
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
                    DiagnosticJson = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    HostSequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    BoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriageInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TriageWorkId = table.Column<Guid>(type: "uuid", nullable: true)
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

            migrationBuilder.CreateIndex(
                name: "IX_WebHostCommands_PreviewId",
                table: "WebHostCommands",
                column: "PreviewId");

            migrationBuilder.CreateIndex(
                name: "IX_WebHostCommands_WebHostId_Status_CreatedAt",
                table: "WebHostCommands",
                columns: new[] { "WebHostId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_IdentityKeyDigest",
                table: "WebHostRegistrations",
                column: "IdentityKeyDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_OrganizationId_RegistrationRequestId",
                table: "WebHostRegistrations",
                columns: new[] { "OrganizationId", "RegistrationRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_OrganizationId_Status_LastHeartbeatAt",
                table: "WebHostRegistrations",
                columns: new[] { "OrganizationId", "Status", "LastHeartbeatAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebHostRegistrations_ProviderInstallationId",
                table: "WebHostRegistrations",
                column: "ProviderInstallationId");

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
    }
}
