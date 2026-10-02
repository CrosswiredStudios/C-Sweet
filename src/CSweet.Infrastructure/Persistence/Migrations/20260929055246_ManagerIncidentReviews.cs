using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManagerIncidentReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActionReference",
                table: "ProjectIncidents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Disposition",
                table: "ProjectIncidents",
                type: "text",
                nullable: false,
                defaultValue: "Escalate");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewAt",
                table: "ProjectIncidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIncidents_Status_ReviewAt",
                table: "ProjectIncidents",
                columns: new[] { "Status", "ReviewAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProjectIncidents_Status_ReviewAt",
                table: "ProjectIncidents");

            migrationBuilder.DropColumn(
                name: "ActionReference",
                table: "ProjectIncidents");

            migrationBuilder.DropColumn(
                name: "Disposition",
                table: "ProjectIncidents");

            migrationBuilder.DropColumn(
                name: "ReviewAt",
                table: "ProjectIncidents");
        }
    }
}
