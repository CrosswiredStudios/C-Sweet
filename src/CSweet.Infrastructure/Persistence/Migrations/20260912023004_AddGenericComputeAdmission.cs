using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGenericComputeAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConstraintsJson",
                table: "ScopedActionGrants",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "ComputeAdmissions",
                columns: table => new
                {
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeAdmissions", x => x.InstallationId);
                    table.ForeignKey(
                        name: "FK_ComputeAdmissions_AgentInstallations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComputeAdmissions_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComputeEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    DesiredEnvironmentKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    SpecificationJson = table.Column<string>(type: "text", nullable: false),
                    Persistence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DesiredState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProviderResourceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastFailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TeardownConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeEnvironments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeEnvironments_AgentInstallations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComputeEnvironments_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComputeEnvironments_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComputeOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthorityJson = table.Column<string>(type: "text", nullable: false),
                    TemplateJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeOperations_ComputeEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "ComputeEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComputeRequestReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeRequestReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeRequestReceipts_ComputeEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "ComputeEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeAdmissions_OrganizationId",
                table: "ComputeAdmissions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeEnvironments_DesiredState_LeaseExpiresAt",
                table: "ComputeEnvironments",
                columns: new[] { "DesiredState", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeEnvironments_InstallationId",
                table: "ComputeEnvironments",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeEnvironments_OrganizationId_InstallationId_DesiredEn~",
                table: "ComputeEnvironments",
                columns: new[] { "OrganizationId", "InstallationId", "DesiredEnvironmentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComputeEnvironments_OrganizationId_InstallationId_Idempoten~",
                table: "ComputeEnvironments",
                columns: new[] { "OrganizationId", "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComputeEnvironments_OrganizationId_InstallationId_Workstrea~",
                table: "ComputeEnvironments",
                columns: new[] { "OrganizationId", "InstallationId", "WorkstreamId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeEnvironments_WorkstreamId",
                table: "ComputeEnvironments",
                column: "WorkstreamId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeOperations_EnvironmentId_Generation",
                table: "ComputeOperations",
                columns: new[] { "EnvironmentId", "Generation" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeOperations_Status_NextAttemptAt",
                table: "ComputeOperations",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeRequestReceipts_EnvironmentId",
                table: "ComputeRequestReceipts",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeRequestReceipts_OrganizationId_InstallationId_Idempo~",
                table: "ComputeRequestReceipts",
                columns: new[] { "OrganizationId", "InstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputeAdmissions");

            migrationBuilder.DropTable(
                name: "ComputeOperations");

            migrationBuilder.DropTable(
                name: "ComputeRequestReceipts");

            migrationBuilder.DropTable(
                name: "ComputeEnvironments");

            migrationBuilder.DropColumn(
                name: "ConstraintsJson",
                table: "ScopedActionGrants");
        }
    }
}
