using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceControlApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_RepositoryProvisioningRequests_OrganizationId_Id",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "Id" });

            migrationBuilder.CreateTable(
                name: "SourceControlApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByAgentInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProvisioningRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    MergeJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlApprovals", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlApprovals_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceControlApprovals_RepositoryProvisioningRequests_Organ~",
                        columns: x => new { x.OrganizationId, x.ProvisioningRequestId },
                        principalTable: "RepositoryProvisioningRequests",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlApprovals_OrganizationId_IdempotencyKey",
                table: "SourceControlApprovals",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlApprovals_OrganizationId_MergeJobId",
                table: "SourceControlApprovals",
                columns: new[] { "OrganizationId", "MergeJobId" },
                unique: true,
                filter: "\"MergeJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlApprovals_OrganizationId_ProvisioningRequestId",
                table: "SourceControlApprovals",
                columns: new[] { "OrganizationId", "ProvisioningRequestId" },
                unique: true,
                filter: "\"ProvisioningRequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlApprovals_OrganizationId_Status_CreatedAt",
                table: "SourceControlApprovals",
                columns: new[] { "OrganizationId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceControlApprovals");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_RepositoryProvisioningRequests_OrganizationId_Id",
                table: "RepositoryProvisioningRequests");
        }
    }
}
