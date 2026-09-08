using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

public sealed partial class AddPluginOperationalStateAvailability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>("AvailableAt", "PluginOperationalStates",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.CreateIndex("IX_PluginOperationalStates_Kind_AvailableAt", "PluginOperationalStates", ["Kind", "AvailableAt"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_PluginOperationalStates_Kind_AvailableAt", "PluginOperationalStates");
        migrationBuilder.DropColumn("AvailableAt", "PluginOperationalStates");
    }
}
