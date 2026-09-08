using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260908220000_AddCompanyDashboard")]
public sealed class AddCompanyDashboard : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("CompanyDashboardReports", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
            Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
            WorkstreamId = table.Column<Guid>(type: "uuid", nullable: true),
            ReporterOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
            ReporterName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
            PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            PayloadJson = table.Column<string>(type: "text", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_CompanyDashboardReports", x => x.Id);
            table.ForeignKey("FK_CompanyDashboardReports_CoreOrganizations_OrganizationId", x => x.OrganizationId,
                "CoreOrganizations", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_CompanyDashboardReports_Latest",
            "CompanyDashboardReports", new[] { "OrganizationId", "Kind", "WorkstreamId", "PublishedAt" });
        migrationBuilder.CreateTable("CompanyDashboardLayouts", columns: table => new
        {
            OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
            OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
            OrderJson = table.Column<string>(type: "text", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_CompanyDashboardLayouts", x => new { x.OrganizationId, x.OrganizationUserId });
            table.ForeignKey("FK_CompanyDashboardLayouts_CoreOrganizations_OrganizationId", x => x.OrganizationId,
                "CoreOrganizations", "Id", onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_CompanyDashboardLayouts_User", x => x.OrganizationUserId,
                "CoreOrganizationUsers", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_CompanyDashboardLayouts_OrganizationUserId", "CompanyDashboardLayouts", "OrganizationUserId");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("CompanyDashboardLayouts");
        migrationBuilder.DropTable("CompanyDashboardReports");
    }
}
