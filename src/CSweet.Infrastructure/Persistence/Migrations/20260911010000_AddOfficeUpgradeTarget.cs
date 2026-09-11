using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

public sealed partial class AddOfficeUpgradeTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "UpgradeOfficeId", table: "LocalOfficeSetupSessions", type: "uuid", nullable: true);
        migrationBuilder.CreateIndex(name: "IX_LocalOfficeSetupSessions_UpgradeOfficeId", table: "LocalOfficeSetupSessions",
            column: "UpgradeOfficeId", unique: true,
            filter: "\"UpgradeOfficeId\" IS NOT NULL AND \"Status\" IN ('Created', 'Redeemed')");
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_LocalOfficeSetupSessions_UpgradeOfficeId", table: "LocalOfficeSetupSessions");
        migrationBuilder.DropColumn(name: "UpgradeOfficeId", table: "LocalOfficeSetupSessions");
    }
}
