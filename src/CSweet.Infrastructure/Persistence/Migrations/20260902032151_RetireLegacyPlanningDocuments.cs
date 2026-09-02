using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireLegacyPlanningDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanningDocuments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanningDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "character varying(131072)", maxLength: 131072, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GeneratedByTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsLatest = table.Column<bool>(type: "boolean", nullable: false),
                    StructuredJson = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true),
                    Summary = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanningDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanningDocuments_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanningDocuments_OrganizationId_DocumentType_IsLatest",
                table: "PlanningDocuments",
                columns: new[] { "OrganizationId", "DocumentType", "IsLatest" });
        }
    }
}
