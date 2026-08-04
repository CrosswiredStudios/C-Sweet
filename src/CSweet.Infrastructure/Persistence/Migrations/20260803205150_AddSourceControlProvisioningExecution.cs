using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceControlProvisioningExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemplateRepositoryId",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "RepositoryProvisioningRequests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "PolicyRevision",
                table: "RepositoryProvisioningRequests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ProjectDisplayName",
                table: "RepositoryProvisioningRequests",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TemplateId",
                table: "RepositoryProvisioningRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "SourceControlRepositoryTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalRepositoryId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DefaultBranch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlRepositoryTemplates", x => x.Id);
                    table.UniqueConstraint("AK_SourceControlRepositoryTemplates_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_SourceControlRepositoryTemplates_SourceControlConnections_O~",
                        columns: x => new { x.OrganizationId, x.ConnectionId },
                        principalTable: "SourceControlConnections",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_TemplateId",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "TemplateId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlRepositoryTemplates_OrganizationId_Connection~1",
                table: "SourceControlRepositoryTemplates",
                columns: new[] { "OrganizationId", "ConnectionId", "Owner", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlRepositoryTemplates_OrganizationId_ConnectionI~",
                table: "SourceControlRepositoryTemplates",
                columns: new[] { "OrganizationId", "ConnectionId", "ExternalRepositoryId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_RepositoryProvisioningRequests_SourceControlRepositoryTempl~",
                table: "RepositoryProvisioningRequests",
                columns: new[] { "OrganizationId", "TemplateId" },
                principalTable: "SourceControlRepositoryTemplates",
                principalColumns: new[] { "OrganizationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RepositoryProvisioningRequests_SourceControlRepositoryTempl~",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.DropTable(
                name: "SourceControlRepositoryTemplates");

            migrationBuilder.DropIndex(
                name: "IX_RepositoryProvisioningRequests_OrganizationId_TemplateId",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.DropColumn(
                name: "ProjectDisplayName",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                table: "RepositoryProvisioningRequests");

            migrationBuilder.AddColumn<string>(
                name: "TemplateRepositoryId",
                table: "RepositoryProvisioningRequests",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
