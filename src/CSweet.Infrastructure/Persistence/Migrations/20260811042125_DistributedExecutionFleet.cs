using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DistributedExecutionFleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionOnboardingMode",
                table: "SystemConfigurations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultBuildExecutionPoolId",
                table: "AgentRuntimeGlobalSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultRuntimeExecutionPoolId",
                table: "AgentRuntimeGlobalSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionPoolId",
                table: "AgentInstallations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionPoolId",
                table: "AgentBuildJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExecutionPools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IsDefaultBuildPool = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefaultRuntimePool = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaximumActiveWorkloads = table.Column<int>(type: "integer", nullable: false),
                    RequiredLabelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    AllowedBusinessIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionPools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionPoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    MachineName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OperatingSystem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NodeVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CertificateSerialNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CertificateExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CertificateSigningRequestPem = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    IssuedCertificateBase64 = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    LabelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    AllocatableCpuCount = table.Column<int>(type: "integer", nullable: false),
                    AllocatableMemoryMb = table.Column<int>(type: "integer", nullable: false),
                    AllocatableDiskMb = table.Column<int>(type: "integer", nullable: false),
                    MaximumConcurrentWorkloads = table.Column<int>(type: "integer", nullable: false),
                    SessionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DrainingAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionNodes_ExecutionPools_ExecutionPoolId",
                        column: x => x.ExecutionPoolId,
                        principalTable: "ExecutionPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionNodeEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionPoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceiptHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionNodeEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionNodeEnrollments_ExecutionNodes_ExecutionNodeId",
                        column: x => x.ExecutionNodeId,
                        principalTable: "ExecutionNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionNodeEnrollments_ExecutionPools_ExecutionPoolId",
                        column: x => x.ExecutionPoolId,
                        principalTable: "ExecutionPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionNodeProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionNodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BrokerProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GuestImageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CertificationSuiteVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CertificationEvidenceDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CertifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CertificationExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupportsBuilderWorkloads = table.Column<bool>(type: "boolean", nullable: false),
                    SupportsRuntimeWorkloads = table.Column<bool>(type: "boolean", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    UnavailableReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionNodeProviders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExecutionNodeProviders_ExecutionNodes_ExecutionNodeId",
                        column: x => x.ExecutionNodeId,
                        principalTable: "ExecutionNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionWorkloadAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionPoolId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentBuildJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentRuntimeInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    BusinessId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WorkloadKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GuestImageDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ArtifactDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SpecificationJson = table.Column<string>(type: "jsonb", nullable: false),
                    SpecificationDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    AssignmentTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactGrantTransferHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ArtifactGrantInUseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArtifactGrantConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingEpoch = table.Column<long>(type: "bigint", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    ReservedCpuCount = table.Column<int>(type: "integer", nullable: false),
                    ReservedMemoryMb = table.Column<int>(type: "integer", nullable: false),
                    ReservedDiskMb = table.Column<int>(type: "integer", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SanitizedFailure = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ProviderInstanceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResultArtifactLocator = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ResultArtifactDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ResultArtifactSignature = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    ResultArtifactFormatVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultArtifactOperatingSystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultArtifactArchitecture = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultLogExcerpt = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionWorkloadAssignments", x => x.Id);
                    table.CheckConstraint("CK_ExecutionWorkloadAssignments_ExactlyOneWorkload", "(\"AgentBuildJobId\" IS NOT NULL AND \"AgentRuntimeInstanceId\" IS NULL) OR (\"AgentBuildJobId\" IS NULL AND \"AgentRuntimeInstanceId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ExecutionWorkloadAssignments_AgentBuildJobs_AgentBuildJobId",
                        column: x => x.AgentBuildJobId,
                        principalTable: "AgentBuildJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExecutionWorkloadAssignments_AgentRuntimeInstances_AgentRun~",
                        column: x => x.AgentRuntimeInstanceId,
                        principalTable: "AgentRuntimeInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExecutionWorkloadAssignments_ExecutionNodes_ExecutionNodeId",
                        column: x => x.ExecutionNodeId,
                        principalTable: "ExecutionNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExecutionWorkloadAssignments_ExecutionPools_ExecutionPoolId",
                        column: x => x.ExecutionPoolId,
                        principalTable: "ExecutionPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuntimeGlobalSettings_DefaultBuildExecutionPoolId",
                table: "AgentRuntimeGlobalSettings",
                column: "DefaultBuildExecutionPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuntimeGlobalSettings_DefaultRuntimeExecutionPoolId",
                table: "AgentRuntimeGlobalSettings",
                column: "DefaultRuntimeExecutionPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstallations_ExecutionPoolId",
                table: "AgentInstallations",
                column: "ExecutionPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentBuildJobs_ExecutionPoolId",
                table: "AgentBuildJobs",
                column: "ExecutionPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodeEnrollments_ExecutionNodeId",
                table: "ExecutionNodeEnrollments",
                column: "ExecutionNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodeEnrollments_ExecutionPoolId_Status_ExpiresAt",
                table: "ExecutionNodeEnrollments",
                columns: new[] { "ExecutionPoolId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodeEnrollments_ReceiptHash",
                table: "ExecutionNodeEnrollments",
                column: "ReceiptHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodeEnrollments_TokenHash",
                table: "ExecutionNodeEnrollments",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodeProviders_ExecutionNodeId_ProviderId_GuestImag~",
                table: "ExecutionNodeProviders",
                columns: new[] { "ExecutionNodeId", "ProviderId", "GuestImageDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodes_CertificateThumbprint",
                table: "ExecutionNodes",
                column: "CertificateThumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodes_ExecutionPoolId_Status",
                table: "ExecutionNodes",
                columns: new[] { "ExecutionPoolId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionPools_IsDefaultBuildPool",
                table: "ExecutionPools",
                column: "IsDefaultBuildPool",
                unique: true,
                filter: "\"IsDefaultBuildPool\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionPools_IsDefaultRuntimePool",
                table: "ExecutionPools",
                column: "IsDefaultRuntimePool",
                unique: true,
                filter: "\"IsDefaultRuntimePool\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionPools_Name",
                table: "ExecutionPools",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWorkloadAssignments_AgentBuildJobId",
                table: "ExecutionWorkloadAssignments",
                column: "AgentBuildJobId",
                unique: true,
                filter: "\"AgentBuildJobId\" IS NOT NULL AND \"Status\" IN ('Pending','Assigned','Starting','Running','Stopping')");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWorkloadAssignments_AgentRuntimeInstanceId",
                table: "ExecutionWorkloadAssignments",
                column: "AgentRuntimeInstanceId",
                unique: true,
                filter: "\"AgentRuntimeInstanceId\" IS NOT NULL AND \"Status\" IN ('Pending','Assigned','Starting','Running','Stopping')");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWorkloadAssignments_ExecutionNodeId",
                table: "ExecutionWorkloadAssignments",
                column: "ExecutionNodeId");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionWorkloadAssignments_ExecutionPoolId_Status_QueuedAt",
                table: "ExecutionWorkloadAssignments",
                columns: new[] { "ExecutionPoolId", "Status", "QueuedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentBuildJobs_ExecutionPools_ExecutionPoolId",
                table: "AgentBuildJobs",
                column: "ExecutionPoolId",
                principalTable: "ExecutionPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentInstallations_ExecutionPools_ExecutionPoolId",
                table: "AgentInstallations",
                column: "ExecutionPoolId",
                principalTable: "ExecutionPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuntimeGlobalSettings_ExecutionPools_DefaultBuildExecu~",
                table: "AgentRuntimeGlobalSettings",
                column: "DefaultBuildExecutionPoolId",
                principalTable: "ExecutionPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuntimeGlobalSettings_ExecutionPools_DefaultRuntimeExe~",
                table: "AgentRuntimeGlobalSettings",
                column: "DefaultRuntimeExecutionPoolId",
                principalTable: "ExecutionPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentBuildJobs_ExecutionPools_ExecutionPoolId",
                table: "AgentBuildJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentInstallations_ExecutionPools_ExecutionPoolId",
                table: "AgentInstallations");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuntimeGlobalSettings_ExecutionPools_DefaultBuildExecu~",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuntimeGlobalSettings_ExecutionPools_DefaultRuntimeExe~",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropTable(
                name: "ExecutionNodeEnrollments");

            migrationBuilder.DropTable(
                name: "ExecutionNodeProviders");

            migrationBuilder.DropTable(
                name: "ExecutionWorkloadAssignments");

            migrationBuilder.DropTable(
                name: "ExecutionNodes");

            migrationBuilder.DropTable(
                name: "ExecutionPools");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuntimeGlobalSettings_DefaultBuildExecutionPoolId",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuntimeGlobalSettings_DefaultRuntimeExecutionPoolId",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropIndex(
                name: "IX_AgentInstallations_ExecutionPoolId",
                table: "AgentInstallations");

            migrationBuilder.DropIndex(
                name: "IX_AgentBuildJobs_ExecutionPoolId",
                table: "AgentBuildJobs");

            migrationBuilder.DropColumn(
                name: "ExecutionOnboardingMode",
                table: "SystemConfigurations");

            migrationBuilder.DropColumn(
                name: "DefaultBuildExecutionPoolId",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropColumn(
                name: "DefaultRuntimeExecutionPoolId",
                table: "AgentRuntimeGlobalSettings");

            migrationBuilder.DropColumn(
                name: "ExecutionPoolId",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "ExecutionPoolId",
                table: "AgentBuildJobs");
        }
    }
}
