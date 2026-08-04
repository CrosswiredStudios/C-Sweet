using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResetSourceControlV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitRepositoryConnectionGrants");

            migrationBuilder.DropTable(
                name: "GitTicketWorkspaces");

            migrationBuilder.DropTable(
                name: "GitRepositoryConnections");

            migrationBuilder.CreateTable(
                name: "SourceControlConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderAccountId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AccountLogin = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceAccessInstallationId = table.Column<long>(type: "bigint", nullable: true),
                    ProvisionerInstallationId = table.Column<long>(type: "bigint", nullable: true),
                    AllowedHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AllowedPort = table.Column<int>(type: "integer", nullable: true),
                    SshHostFingerprintsJson = table.Column<string>(type: "jsonb", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHealthError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisconnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlConnections", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlConnections_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "SourceControlOnboardingSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentStep = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StateNonceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DraftJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlOnboardingSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryProvisioningPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    NamePrefix = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NamingPattern = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ApprovedTemplatesJson = table.Column<string>(type: "jsonb", nullable: false),
                    MaximumRepositories = table.Column<int>(type: "integer", nullable: false),
                    RequiresManagerApproval = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryProvisioningPolicies", x => x.Id);
                    table.UniqueConstraint("AK_RepositoryProvisioningPolicies_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_RepositoryProvisioningPolicies_SourceControlConnections_Org~",
                        columns: x => new { x.OrganizationId, x.ConnectionId },
                        principalTable: "SourceControlConnections",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    ProtectionVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceControlCredentials_SourceControlConnections_Organizat~",
                        columns: x => new { x.OrganizationId, x.ConnectionId },
                        principalTable: "SourceControlConnections",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlRepositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalRepositoryId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CanonicalPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CloneUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsPrivate = table.Column<bool>(type: "boolean", nullable: false),
                    IsManaged = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHealthError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlRepositories", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlRepositories_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceControlRepositories_SourceControlConnections_Organiza~",
                        columns: x => new { x.OrganizationId, x.ConnectionId },
                        principalTable: "SourceControlConnections",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryProvisioningRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByAgentInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepositoryName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TemplateRepositoryId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryProvisioningRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepositoryProvisioningRequests_RepositoryProvisioningPolici~",
                        columns: x => new { x.OrganizationId, x.PolicyId },
                        principalTable: "RepositoryProvisioningPolicies",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepositoryProvisioningRequests_SourceControlConnections_Org~",
                        columns: x => new { x.OrganizationId, x.ConnectionId },
                        principalTable: "SourceControlConnections",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepositoryProvisioningRequests_SourceControlRepositories_Or~",
                        columns: x => new { x.OrganizationId, x.RepositoryId },
                        principalTable: "SourceControlRepositories",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentRevision = table.Column<long>(type: "bigint", nullable: false),
                    WorkspaceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BaseCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BranchName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlWorkspaces", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlWorkspaces_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceControlWorkspaces_SourceControlRepositories_Organizat~",
                        columns: x => new { x.OrganizationId, x.RepositoryId },
                        principalTable: "SourceControlRepositories",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeamRepositoryPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    MergeApprovalMode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamRepositoryPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamRepositoryPolicies_SourceControlRepositories_Organizati~",
                        columns: x => new { x.OrganizationId, x.RepositoryId },
                        principalTable: "SourceControlRepositories",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlPublications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TicketBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PullRequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PullRequestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ChangedFilesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ValidationResultsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlPublications", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlPublications_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceControlPublications_SourceControlRepositories_Organiz~",
                        columns: x => new { x.OrganizationId, x.RepositoryId },
                        principalTable: "SourceControlRepositories",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceControlPublications_SourceControlWorkspaces_Organizat~",
                        columns: x => new { x.OrganizationId, x.WorkspaceId },
                        principalTable: "SourceControlWorkspaces",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlMergeAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TeamPolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    DecisionSignature = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlMergeAuthorizations", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlMergeAuthorizations_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceControlMergeAuthorizations_SourceControlPublications_~",
                        columns: x => new { x.OrganizationId, x.PublicationId },
                        principalTable: "SourceControlPublications",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlValidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValidatorAgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResultsJson = table.Column<string>(type: "jsonb", nullable: false),
                    FailureMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlValidations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceControlValidations_SourceControlPublications_Organiza~",
                        columns: x => new { x.OrganizationId, x.PublicationId },
                        principalTable: "SourceControlPublications",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceControlMergeJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadAuthorizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdministratorApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpectedHeadSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovalMode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    MergeCommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlMergeJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceControlMergeJobs_SourceControlMergeAuthorizations_Org~",
                        columns: x => new { x.OrganizationId, x.LeadAuthorizationId },
                        principalTable: "SourceControlMergeAuthorizations",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceControlMergeJobs_SourceControlPublications_Organizati~",
                        columns: x => new { x.OrganizationId, x.PublicationId },
                        principalTable: "SourceControlPublications",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningPolicies_OrganizationId_ConnectionId",
                table: "RepositoryProvisioningPolicies",
                columns: new[] { "OrganizationId", "ConnectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_ConnectionId",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "ConnectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_IdempotencyKey",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_PolicyId",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "PolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_RepositoryId",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "RepositoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_Status_Create~",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlConnections_OrganizationId_Name",
                table: "SourceControlConnections",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlConnections_OrganizationId_Provider_ProviderAc~",
                table: "SourceControlConnections",
                columns: new[] { "OrganizationId", "Provider", "ProviderAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlCredentials_OrganizationId_ConnectionId_Revoke~",
                table: "SourceControlCredentials",
                columns: new[] { "OrganizationId", "ConnectionId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlMergeAuthorizations_OrganizationId_ExpiresAt_R~",
                table: "SourceControlMergeAuthorizations",
                columns: new[] { "OrganizationId", "ExpiresAt", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlMergeAuthorizations_OrganizationId_Publication~",
                table: "SourceControlMergeAuthorizations",
                columns: new[] { "OrganizationId", "PublicationId", "AuthorizedByOrganizationUserId", "CommitSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlMergeJobs_OrganizationId_IdempotencyKey",
                table: "SourceControlMergeJobs",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlMergeJobs_OrganizationId_LeadAuthorizationId",
                table: "SourceControlMergeJobs",
                columns: new[] { "OrganizationId", "LeadAuthorizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlMergeJobs_OrganizationId_PublicationId_Expecte~",
                table: "SourceControlMergeJobs",
                columns: new[] { "OrganizationId", "PublicationId", "ExpectedHeadSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlOnboardingSessions_OrganizationId_StartedByOrg~",
                table: "SourceControlOnboardingSessions",
                columns: new[] { "OrganizationId", "StartedByOrganizationUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlOnboardingSessions_StateNonceHash_ExpiresAt",
                table: "SourceControlOnboardingSessions",
                columns: new[] { "StateNonceHash", "ExpiresAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlPublications_OrganizationId_RepositoryId",
                table: "SourceControlPublications",
                columns: new[] { "OrganizationId", "RepositoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlPublications_OrganizationId_WorkspaceId_Commit~",
                table: "SourceControlPublications",
                columns: new[] { "OrganizationId", "WorkspaceId", "CommitSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlRepositories_OrganizationId_CanonicalPath",
                table: "SourceControlRepositories",
                columns: new[] { "OrganizationId", "CanonicalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlRepositories_OrganizationId_ConnectionId_Exter~",
                table: "SourceControlRepositories",
                columns: new[] { "OrganizationId", "ConnectionId", "ExternalRepositoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlValidations_OrganizationId_PublicationId_Valid~",
                table: "SourceControlValidations",
                columns: new[] { "OrganizationId", "PublicationId", "ValidatorAgentInstallationId", "CommitSha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlWorkspaces_OrganizationId_AgentInstallationId_~",
                table: "SourceControlWorkspaces",
                columns: new[] { "OrganizationId", "AgentInstallationId", "WorkItemId", "AssignmentRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlWorkspaces_OrganizationId_RepositoryId",
                table: "SourceControlWorkspaces",
                columns: new[] { "OrganizationId", "RepositoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlWorkspaces_WorkspaceKey",
                table: "SourceControlWorkspaces",
                column: "WorkspaceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamRepositoryPolicies_OrganizationId_RepositoryId",
                table: "TeamRepositoryPolicies",
                columns: new[] { "OrganizationId", "RepositoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamRepositoryPolicies_OrganizationId_TeamId_IsPrimary",
                table: "TeamRepositoryPolicies",
                columns: new[] { "OrganizationId", "TeamId", "IsPrimary" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"DisabledAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TeamRepositoryPolicies_OrganizationId_TeamId_RepositoryId",
                table: "TeamRepositoryPolicies",
                columns: new[] { "OrganizationId", "TeamId", "RepositoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryProvisioningRequests");

            migrationBuilder.DropTable(
                name: "SourceControlCredentials");

            migrationBuilder.DropTable(
                name: "SourceControlMergeJobs");

            migrationBuilder.DropTable(
                name: "SourceControlOnboardingSessions");

            migrationBuilder.DropTable(
                name: "SourceControlValidations");

            migrationBuilder.DropTable(
                name: "TeamRepositoryPolicies");

            migrationBuilder.DropTable(
                name: "RepositoryProvisioningPolicies");

            migrationBuilder.DropTable(
                name: "SourceControlMergeAuthorizations");

            migrationBuilder.DropTable(
                name: "SourceControlPublications");

            migrationBuilder.DropTable(
                name: "SourceControlWorkspaces");

            migrationBuilder.DropTable(
                name: "SourceControlRepositories");

            migrationBuilder.DropTable(
                name: "SourceControlConnections");

            migrationBuilder.CreateTable(
                name: "GitRepositoryConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedHostsJson = table.Column<string>(type: "text", nullable: false),
                    AllowedOperations = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AllowedPortsJson = table.Column<string>(type: "text", nullable: false),
                    AuthenticationMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CloneUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermittedRepositoryPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PullRequestProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SshHostFingerprintsJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitRepositoryConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitRepositoryConnectionGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanMergeQaApprovedPullRequest = table.Column<bool>(type: "boolean", nullable: false),
                    CanPushTicketBranch = table.Column<bool>(type: "boolean", nullable: false),
                    CanReadFetch = table.Column<bool>(type: "boolean", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitRepositoryConnectionGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitRepositoryConnectionGrants_AgentInstallations_AgentInsta~",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitRepositoryConnectionGrants_GitRepositoryConnections_Repo~",
                        column: x => x.RepositoryConnectionId,
                        principalTable: "GitRepositoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitTicketWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentRevision = table.Column<long>(type: "bigint", nullable: false),
                    BaseBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BranchName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ChangedFilesJson = table.Column<string>(type: "text", nullable: false),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    MergeCommitSha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MergeStatus = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MergedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PullRequestUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidationsJson = table.Column<string>(type: "text", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspacePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitTicketWorkspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitTicketWorkspaces_AgentInstallations_AgentInstallationId",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitTicketWorkspaces_GitRepositoryConnections_RepositoryConn~",
                        column: x => x.RepositoryConnectionId,
                        principalTable: "GitRepositoryConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositoryConnectionGrants_AgentInstallationId",
                table: "GitRepositoryConnectionGrants",
                column: "AgentInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositoryConnectionGrants_RepositoryConnectionId_AgentI~",
                table: "GitRepositoryConnectionGrants",
                columns: new[] { "RepositoryConnectionId", "AgentInstallationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitRepositoryConnections_OrganizationId_Name",
                table: "GitRepositoryConnections",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitTicketWorkspaces_AgentInstallationId_WorkItemId_Assignme~",
                table: "GitTicketWorkspaces",
                columns: new[] { "AgentInstallationId", "WorkItemId", "AssignmentRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitTicketWorkspaces_RepositoryConnectionId",
                table: "GitTicketWorkspaces",
                column: "RepositoryConnectionId");
        }
    }
}
