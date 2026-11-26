using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20261126051000_AttestOfficeComputeFeatures")]
public sealed class AttestOfficeComputeFeatures : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>("ComputeReservationFeaturesJson", "LocalOfficeSetupSessions", type: "text", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("ComputeReservationFeaturesJson", "LocalOfficeSetupSessions");
}
