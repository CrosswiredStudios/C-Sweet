using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestrationRetryIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "WorkOrchestrationEvents",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrchestrationEvents_OrganizationId_EventType_Idempotenc~",
                table: "WorkOrchestrationEvents",
                columns: new[] { "OrganizationId", "EventType", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkOrchestrationEvents_OrganizationId_EventType_Idempotenc~",
                table: "WorkOrchestrationEvents");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "WorkOrchestrationEvents");
        }
    }
}
