using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

public partial class OfficeComputeTemplate : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.AddColumn<string>("ComputeTemplateId", "ExecutionNodes", type: "character varying(64)", maxLength: 64, nullable: true);
        builder.AddColumn<string>("ComputeCapacityJson", "ExecutionNodes", type: "jsonb", nullable: true);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.DropColumn("ComputeTemplateId", "ExecutionNodes");
        builder.DropColumn("ComputeCapacityJson", "ExecutionNodes");
    }
}
