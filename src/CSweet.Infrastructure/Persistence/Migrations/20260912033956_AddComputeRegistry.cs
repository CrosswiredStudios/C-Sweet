using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputeRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComputeNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VerificationPublicKeyBase64 = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeNodes", x => x.Id);
                    table.UniqueConstraint("AK_ComputeNodes_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_ComputeNodes_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComputeTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TemplateJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeTemplates", x => x.Id);
                    table.UniqueConstraint("AK_ComputeTemplates_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_ComputeTemplates_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComputeTemplatePlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateRegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeTemplatePlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComputeTemplatePlacements_ComputeNodes_NodeId_OrganizationId",
                        columns: x => new { x.NodeId, x.OrganizationId },
                        principalTable: "ComputeNodes",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComputeTemplatePlacements_ComputeTemplates_TemplateRegistra~",
                        columns: x => new { x.TemplateRegistrationId, x.OrganizationId },
                        principalTable: "ComputeTemplates",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeNodes_OrganizationId",
                table: "ComputeNodes",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTemplatePlacements_NodeId_OrganizationId",
                table: "ComputeTemplatePlacements",
                columns: new[] { "NodeId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTemplatePlacements_TemplateRegistrationId_NodeId",
                table: "ComputeTemplatePlacements",
                columns: new[] { "TemplateRegistrationId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTemplatePlacements_TemplateRegistrationId_Organizati~",
                table: "ComputeTemplatePlacements",
                columns: new[] { "TemplateRegistrationId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeTemplates_OrganizationId_TemplateId",
                table: "ComputeTemplates",
                columns: new[] { "OrganizationId", "TemplateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputeTemplatePlacements");

            migrationBuilder.DropTable(
                name: "ComputeNodes");

            migrationBuilder.DropTable(
                name: "ComputeTemplates");
        }
    }
}
