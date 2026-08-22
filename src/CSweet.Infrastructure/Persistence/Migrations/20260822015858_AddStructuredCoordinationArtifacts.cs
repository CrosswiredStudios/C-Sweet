using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStructuredCoordinationArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactDigest",
                table: "AgentCoordinationTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ArtifactIsFinalPage",
                table: "AgentCoordinationTurns",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtifactKey",
                table: "AgentCoordinationTurns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ArtifactPageOrdinal",
                table: "AgentCoordinationTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtifactPayloadJson",
                table: "AgentCoordinationTurns",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtifactSchemaVersion",
                table: "AgentCoordinationTurns",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtifactType",
                table: "AgentCoordinationTurns",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~",
                table: "AgentCoordinationTurns",
                columns: new[] { "SessionId", "ArtifactType", "ArtifactKey", "ArtifactPageOrdinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactDigest",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactIsFinalPage",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactKey",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactPageOrdinal",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactPayloadJson",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactSchemaVersion",
                table: "AgentCoordinationTurns");

            migrationBuilder.DropColumn(
                name: "ArtifactType",
                table: "AgentCoordinationTurns");
        }
    }
}
