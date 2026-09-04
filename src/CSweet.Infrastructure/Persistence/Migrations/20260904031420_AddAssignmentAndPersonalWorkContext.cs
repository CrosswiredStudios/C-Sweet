using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentAndPersonalWorkContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequirementsJson",
                table: "WorkItemStageAssignments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectionEvidenceJson",
                table: "WorkItemStageAssignments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EstimateProvenanceJson",
                table: "CoreWorkTasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsExecutable",
                table: "CoreWorkTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PersonalWorkContextJson",
                table: "CoreWorkTasks",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequirementsJson",
                table: "WorkItemStageAssignments");

            migrationBuilder.DropColumn(
                name: "SelectionEvidenceJson",
                table: "WorkItemStageAssignments");

            migrationBuilder.DropColumn(
                name: "EstimateProvenanceJson",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "IsExecutable",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "PersonalWorkContextJson",
                table: "CoreWorkTasks");
        }
    }
}
