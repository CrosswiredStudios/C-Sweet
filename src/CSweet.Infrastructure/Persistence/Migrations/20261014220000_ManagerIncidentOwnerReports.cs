using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20261014220000_ManagerIncidentOwnerReports")]
public sealed class ManagerIncidentOwnerReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("OwnerReportJson", "ProjectIncidents", type: "text", nullable: true);
        migrationBuilder.AddColumn<Guid>("ReporterOrganizationUserId", "ProjectIncidents", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<long>("ReportRevision", "ProjectIncidents", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<string>("ResolutionOutcome", "ProjectIncidents", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("OwnerReportRequestedAt", "ProjectIncidents", type: "timestamp with time zone", nullable: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("OwnerReportJson", "ProjectIncidents");
        migrationBuilder.DropColumn("ReporterOrganizationUserId", "ProjectIncidents");
        migrationBuilder.DropColumn("ReportRevision", "ProjectIncidents");
        migrationBuilder.DropColumn("ResolutionOutcome", "ProjectIncidents");
        migrationBuilder.DropColumn("OwnerReportRequestedAt", "ProjectIncidents");
    }
}
