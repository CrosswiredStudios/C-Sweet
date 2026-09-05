using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessSourceControlDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsedBusinessDefault",
                table: "RepositoryProvisioningRequests",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceControlBusinessSettings",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DefaultTemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceControlBusinessSettings", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_SourceControlBusinessSettings_SourceControlRepositoryTempla~",
                        columns: x => new { x.OrganizationId, x.DefaultTemplateId },
                        principalTable: "SourceControlRepositoryTemplates",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceControlBusinessSettings_OrganizationId_DefaultTemplat~",
                table: "SourceControlBusinessSettings",
                columns: new[] { "OrganizationId", "DefaultTemplateId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceControlBusinessSettings");

            migrationBuilder.DropColumn(
                name: "UsedBusinessDefault",
                table: "RepositoryProvisioningRequests");
        }
    }
}
