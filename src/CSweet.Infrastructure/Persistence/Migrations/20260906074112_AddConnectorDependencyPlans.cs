using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorDependencyPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DependencyId",
                table: "AgentCapabilityBindings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderPackageDigest",
                table: "AgentCapabilityBindings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConnectorExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantRevision = table.Column<long>(type: "bigint", nullable: false),
                    PackageDigest = table.Column<string>(type: "text", nullable: false),
                    Capability = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResourceId = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    InputHash = table.Column<string>(type: "text", nullable: false),
                    PlanHash = table.Column<string>(type: "text", nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConnectorProfileApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageDigest = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProfileId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorProfileApprovals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorExecutions_OrganizationId_RequesterInstallationId_~",
                table: "ConnectorExecutions",
                columns: new[] { "OrganizationId", "RequesterInstallationId", "ConnectorInstallationId", "Capability", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorExecutions_Status_UpdatedAt",
                table: "ConnectorExecutions",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorProfileApprovals_ConnectorInstallationId_PackageDi~",
                table: "ConnectorProfileApprovals",
                columns: new[] { "ConnectorInstallationId", "PackageDigest", "ProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectorExecutions");

            migrationBuilder.DropTable(
                name: "ConnectorProfileApprovals");

            migrationBuilder.DropColumn(
                name: "DependencyId",
                table: "AgentCapabilityBindings");

            migrationBuilder.DropColumn(
                name: "ProviderPackageDigest",
                table: "AgentCapabilityBindings");
        }
    }
}
