using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260729050000_ConstrainWorkTaskEnumValues")]
public partial class ConstrainWorkTaskEnumValues : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint(
            name: "CK_CoreWorkTasks_Kind",
            table: "CoreWorkTasks",
            sql: "\"Kind\" IN ('Initiative', 'Epic', 'Story', 'Task', 'Bug')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_CoreWorkTasks_Status",
            table: "CoreWorkTasks",
            sql: "\"Status\" IN ('Backlog', 'Ready', 'Assigned', 'Running', 'WaitingForApproval', 'Completed', 'Failed', 'Cancelled')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_CoreWorkTasks_Priority",
            table: "CoreWorkTasks",
            sql: "\"Priority\" IN ('Low', 'Medium', 'High', 'Critical')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_CoreWorkTasks_Kind",
            table: "CoreWorkTasks");

        migrationBuilder.DropCheckConstraint(
            name: "CK_CoreWorkTasks_Status",
            table: "CoreWorkTasks");

        migrationBuilder.DropCheckConstraint(
            name: "CK_CoreWorkTasks_Priority",
            table: "CoreWorkTasks");
    }
}
