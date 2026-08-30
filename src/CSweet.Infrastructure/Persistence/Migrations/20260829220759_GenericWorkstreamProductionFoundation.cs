using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GenericWorkstreamProductionFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GenAiOperationConfigurations_ProviderProfileId_OperationTyp~",
                table: "GenAiOperationConfigurations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionWorkloadAssignments_ExactlyOneWorkload",
                table: "ExecutionWorkloadAssignments");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                table: "ManagedExternalResources",
                newName: "WorkstreamId");

            migrationBuilder.RenameColumn(
                name: "ProjectId",
                table: "CoreConversations",
                newName: "WorkstreamId");

            migrationBuilder.AddColumn<string>(
                name: "ProfileDataJson",
                table: "Workstreams",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileDefinitionDigest",
                table: "Workstreams",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileKey",
                table: "Workstreams",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProfileVersion",
                table: "Workstreams",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Workstreams",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceProposalId",
                table: "Workstreams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArtifactId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BuildId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "MediaAssets",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkItemId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "MediaAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationTypeKey",
                table: "GenAiOperationConfigurations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OperationTypeKey",
                table: "GenAiJobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderProfileId",
                table: "GenAiJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "GenAiJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkItemId",
                table: "GenAiJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "GenAiJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryBuildId",
                table: "ExecutionWorkloadAssignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsToolchainBuildWorkloads",
                table: "ExecutionNodeProviders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "CoreArtifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "ArtifactPackages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "ArtifactPackages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkstreamId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArtifactReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RubricTypeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Disposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Comment = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ReviewerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceConversationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactReviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryBuilds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToolchainDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CertificationRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    CertificationPass = table.Column<int>(type: "integer", nullable: true),
                    CertificationFixtureKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CertificationFixtureResource = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceRevision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecipeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                    DefinitionDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    MaximumAttempts = table.Column<int>(type: "integer", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OutputsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ProvenanceJson = table.Column<string>(type: "jsonb", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FailureSummary = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CancelRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryBuilds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryValidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    FindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryValidations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    TypeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    ConsentPolicyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReportJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaAssetReferenceGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurposeTypeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAssetReferenceGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PreviewSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccessReference = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreviewSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseReadinessRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    FindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseReadinessRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolchainAdapterDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderPackageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderPackageVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    DefinitionDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolchainAdapterDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolchainCertificationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolchainDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentProfileKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnvironmentImageDigest = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderPackageDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    DefinitionDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChecksJson = table.Column<string>(type: "jsonb", nullable: false),
                    FirstManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SecondManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolchainCertificationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolchainInstallationEligibilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolchainDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CertificationRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentProfileKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnvironmentImageDigest = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CertifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolchainInstallationEligibilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamAuthorityEnvelopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaximumBudgetVariance = table.Column<decimal>(type: "numeric", nullable: true),
                    MaximumScheduleVarianceDays = table.Column<int>(type: "integer", nullable: true),
                    AuthorizedStaffingRoleKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    HumanRequiredActionKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    AgentAuthorizedActionKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamAuthorityEnvelopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AuthorityRuleKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecommendedOptionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SelectedOptionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    TypeDataJson = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    BlockingImpact = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersedesDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededByDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamGates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    MilestoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LifecycleStage = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequiredEvidenceTypeKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequiredReviewerRoleKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    FindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SubmissionSummary = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    DecisionRationale = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    SubmittedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamGates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamMilestones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LifecycleStage = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TargetDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequiredEvidenceTypeKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequiredReviewerRoleKeysJson = table.Column<string>(type: "jsonb", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamMilestones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamProfileDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MetadataSchemaJson = table.Column<string>(type: "jsonb", nullable: false),
                    LifecyclePolicyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DefaultBoardProfileKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AuthorityPolicyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderPackageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderPackageVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamProfileDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamSupervisionAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupervisorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamSupervisionAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkstreamTeamAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkstreamTeamAssignments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workstreams_OrganizationId_ProfileKey_Status",
                table: "Workstreams",
                columns: new[] { "OrganizationId", "ProfileKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Workstreams_SourceProposalId",
                table: "Workstreams",
                column: "SourceProposalId",
                unique: true,
                filter: "\"SourceProposalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_OrganizationId_WorkstreamId_CreatedAt",
                table: "MediaAssets",
                columns: new[] { "OrganizationId", "WorkstreamId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GenAiOperationConfigurations_ProviderProfileId_OperationTyp~",
                table: "GenAiOperationConfigurations",
                columns: new[] { "ProviderProfileId", "OperationTypeKey", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GenAiJobs_OrganizationId_WorkstreamId_CreatedAt",
                table: "GenAiJobs",
                columns: new[] { "OrganizationId", "WorkstreamId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWorkloadAssignments_DeliveryBuildId",
                table: "ExecutionWorkloadAssignments",
                column: "DeliveryBuildId",
                unique: true,
                filter: "\"DeliveryBuildId\" IS NOT NULL AND \"Status\" IN ('Pending','Assigned','Starting','Running','Stopping')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionWorkloadAssignments_ExactlyOneWorkload",
                table: "ExecutionWorkloadAssignments",
                sql: "(\"WorkloadKind\" = 'Builder' AND \"AgentBuildJobId\" IS NOT NULL AND \"AgentRuntimeInstanceId\" IS NULL AND \"DeliveryBuildId\" IS NULL) OR (\"WorkloadKind\" = 'Runtime' AND \"AgentBuildJobId\" IS NULL AND \"AgentRuntimeInstanceId\" IS NOT NULL AND \"DeliveryBuildId\" IS NULL) OR (\"WorkloadKind\" = 'ToolchainBuild' AND \"AgentBuildJobId\" IS NULL AND \"AgentRuntimeInstanceId\" IS NOT NULL AND \"DeliveryBuildId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CoreArtifacts_OrganizationId_WorkstreamId_DocumentType",
                table: "CoreArtifacts",
                columns: new[] { "OrganizationId", "WorkstreamId", "DocumentType" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactPackages_OrganizationId_WorkstreamId_PackageType",
                table: "ArtifactPackages",
                columns: new[] { "OrganizationId", "WorkstreamId", "PackageType" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactReviews_ArtifactId_RevisionId_CreatedAt",
                table: "ArtifactReviews",
                columns: new[] { "ArtifactId", "RevisionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactReviews_OrganizationId_IdempotencyKey",
                table: "ArtifactReviews",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryBuilds_CertificationRunId_CertificationPass",
                table: "DeliveryBuilds",
                columns: new[] { "CertificationRunId", "CertificationPass" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryBuilds_OrganizationId_RequestedByOrganizationUserId~",
                table: "DeliveryBuilds",
                columns: new[] { "OrganizationId", "RequestedByOrganizationUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryBuilds_WorkstreamId_CreatedAt",
                table: "DeliveryBuilds",
                columns: new[] { "WorkstreamId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryValidations_BuildId_TypeKey",
                table: "DeliveryValidations",
                columns: new[] { "BuildId", "TypeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessions_OrganizationId_CreatedByOrganizationUser~",
                table: "EvaluationSessions",
                columns: new[] { "OrganizationId", "CreatedByOrganizationUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetReferenceGrants_AssetId_WorkstreamId",
                table: "MediaAssetReferenceGrants",
                columns: new[] { "AssetId", "WorkstreamId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssetReferenceGrants_OrganizationId_AgentInstallationI~",
                table: "MediaAssetReferenceGrants",
                columns: new[] { "OrganizationId", "AgentInstallationId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PreviewSessions_BuildId_ExpiresAt",
                table: "PreviewSessions",
                columns: new[] { "BuildId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PreviewSessions_OrganizationId_IdempotencyKey",
                table: "PreviewSessions",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseReadinessRecords_OrganizationId_WorkstreamId_TypeKey",
                table: "ReleaseReadinessRecords",
                columns: new[] { "OrganizationId", "WorkstreamId", "TypeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolchainAdapterDefinitions_Key_Version_ProviderPackageId_P~",
                table: "ToolchainAdapterDefinitions",
                columns: new[] { "Key", "Version", "ProviderPackageId", "ProviderPackageVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolchainCertificationRuns_OrganizationId_Status_CreatedAt",
                table: "ToolchainCertificationRuns",
                columns: new[] { "OrganizationId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolchainCertificationRuns_ToolchainDefinitionId_ProviderIn~",
                table: "ToolchainCertificationRuns",
                columns: new[] { "ToolchainDefinitionId", "ProviderInstallationId", "EnvironmentImageDigest", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolchainInstallationEligibilities_OrganizationId_Toolchain~",
                table: "ToolchainInstallationEligibilities",
                columns: new[] { "OrganizationId", "ToolchainDefinitionId", "ProviderInstallationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamAuthorityEnvelopes_WorkstreamId",
                table: "WorkstreamAuthorityEnvelopes",
                column: "WorkstreamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamDecisions_OrganizationId_RequestedByOrganizationU~",
                table: "WorkstreamDecisions",
                columns: new[] { "OrganizationId", "RequestedByOrganizationUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamDecisions_WorkstreamId_Status_DueAt",
                table: "WorkstreamDecisions",
                columns: new[] { "WorkstreamId", "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamGates_WorkstreamId_Key",
                table: "WorkstreamGates",
                columns: new[] { "WorkstreamId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamMilestones_WorkstreamId_Key",
                table: "WorkstreamMilestones",
                columns: new[] { "WorkstreamId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamProfileDefinitions_DefinitionDigest",
                table: "WorkstreamProfileDefinitions",
                column: "DefinitionDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamProfileDefinitions_Key_Version",
                table: "WorkstreamProfileDefinitions",
                columns: new[] { "Key", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamSupervisionAssignments_SupervisorOrganizationUser~",
                table: "WorkstreamSupervisionAssignments",
                columns: new[] { "SupervisorOrganizationUserId", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamSupervisionAssignments_WorkstreamId_SupervisorOrg~",
                table: "WorkstreamSupervisionAssignments",
                columns: new[] { "WorkstreamId", "SupervisorOrganizationUserId", "RoleKey" },
                unique: true,
                filter: "\"EndsAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamTeamAssignments_WorkstreamId",
                table: "WorkstreamTeamAssignments",
                column: "WorkstreamId",
                unique: true,
                filter: "\"EndsAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkstreamTeamAssignments_WorkstreamId_TeamId_StartsAt",
                table: "WorkstreamTeamAssignments",
                columns: new[] { "WorkstreamId", "TeamId", "StartsAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtifactReviews");

            migrationBuilder.DropTable(
                name: "DeliveryBuilds");

            migrationBuilder.DropTable(
                name: "DeliveryValidations");

            migrationBuilder.DropTable(
                name: "EvaluationSessions");

            migrationBuilder.DropTable(
                name: "MediaAssetReferenceGrants");

            migrationBuilder.DropTable(
                name: "PreviewSessions");

            migrationBuilder.DropTable(
                name: "ReleaseReadinessRecords");

            migrationBuilder.DropTable(
                name: "ToolchainAdapterDefinitions");

            migrationBuilder.DropTable(
                name: "ToolchainCertificationRuns");

            migrationBuilder.DropTable(
                name: "ToolchainInstallationEligibilities");

            migrationBuilder.DropTable(
                name: "WorkstreamAuthorityEnvelopes");

            migrationBuilder.DropTable(
                name: "WorkstreamDecisions");

            migrationBuilder.DropTable(
                name: "WorkstreamGates");

            migrationBuilder.DropTable(
                name: "WorkstreamMilestones");

            migrationBuilder.DropTable(
                name: "WorkstreamProfileDefinitions");

            migrationBuilder.DropTable(
                name: "WorkstreamSupervisionAssignments");

            migrationBuilder.DropTable(
                name: "WorkstreamTeamAssignments");

            migrationBuilder.DropIndex(
                name: "IX_Workstreams_OrganizationId_ProfileKey_Status",
                table: "Workstreams");

            migrationBuilder.DropIndex(
                name: "IX_Workstreams_SourceProposalId",
                table: "Workstreams");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_OrganizationId_WorkstreamId_CreatedAt",
                table: "MediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_GenAiOperationConfigurations_ProviderProfileId_OperationTyp~",
                table: "GenAiOperationConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_GenAiJobs_OrganizationId_WorkstreamId_CreatedAt",
                table: "GenAiJobs");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionWorkloadAssignments_DeliveryBuildId",
                table: "ExecutionWorkloadAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ExecutionWorkloadAssignments_ExactlyOneWorkload",
                table: "ExecutionWorkloadAssignments");

            migrationBuilder.DropIndex(
                name: "IX_CoreArtifacts_OrganizationId_WorkstreamId_DocumentType",
                table: "CoreArtifacts");

            migrationBuilder.DropIndex(
                name: "IX_ArtifactPackages_OrganizationId_WorkstreamId_PackageType",
                table: "ArtifactPackages");

            migrationBuilder.DropColumn(
                name: "ProfileDataJson",
                table: "Workstreams");

            migrationBuilder.DropColumn(
                name: "ProfileDefinitionDigest",
                table: "Workstreams");

            migrationBuilder.DropColumn(
                name: "ProfileKey",
                table: "Workstreams");

            migrationBuilder.DropColumn(
                name: "ProfileVersion",
                table: "Workstreams");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Workstreams");

            migrationBuilder.DropColumn(
                name: "SourceProposalId",
                table: "Workstreams");

            migrationBuilder.DropColumn(
                name: "ArtifactId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "BuildId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "WorkItemId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "MediaAssets");

            migrationBuilder.DropColumn(
                name: "OperationTypeKey",
                table: "GenAiOperationConfigurations");

            migrationBuilder.DropColumn(
                name: "OperationTypeKey",
                table: "GenAiJobs");

            migrationBuilder.DropColumn(
                name: "ProviderProfileId",
                table: "GenAiJobs");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "GenAiJobs");

            migrationBuilder.DropColumn(
                name: "WorkItemId",
                table: "GenAiJobs");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "GenAiJobs");

            migrationBuilder.DropColumn(
                name: "DeliveryBuildId",
                table: "ExecutionWorkloadAssignments");

            migrationBuilder.DropColumn(
                name: "SupportsToolchainBuildWorkloads",
                table: "ExecutionNodeProviders");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "CoreArtifacts");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "ArtifactPackages");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "ArtifactPackages");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "AgentCoordinationSessions");

            migrationBuilder.DropColumn(
                name: "WorkstreamId",
                table: "AgentCoordinationSessions");

            migrationBuilder.RenameColumn(
                name: "WorkstreamId",
                table: "ManagedExternalResources",
                newName: "ProjectId");

            migrationBuilder.RenameColumn(
                name: "WorkstreamId",
                table: "CoreConversations",
                newName: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GenAiOperationConfigurations_ProviderProfileId_OperationTyp~",
                table: "GenAiOperationConfigurations",
                columns: new[] { "ProviderProfileId", "OperationType", "Name" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ExecutionWorkloadAssignments_ExactlyOneWorkload",
                table: "ExecutionWorkloadAssignments",
                sql: "(\"AgentBuildJobId\" IS NOT NULL AND \"AgentRuntimeInstanceId\" IS NULL) OR (\"AgentBuildJobId\" IS NULL AND \"AgentRuntimeInstanceId\" IS NOT NULL)");
        }
    }
}
