using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20261024120000_AddOfficeInstallationStorage")]
public sealed class AddOfficeInstallationStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "StorageVolumeId", table: "LocalOfficeSetupSessions", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "InstallRoot", table: "LocalOfficeSetupSessions", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "DataRoot", table: "LocalOfficeSetupSessions", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "StorageVolumeId", table: "LocalOfficeSetupSessions");
        migrationBuilder.DropColumn(name: "InstallRoot", table: "LocalOfficeSetupSessions");
        migrationBuilder.DropColumn(name: "DataRoot", table: "LocalOfficeSetupSessions");
    }
}
