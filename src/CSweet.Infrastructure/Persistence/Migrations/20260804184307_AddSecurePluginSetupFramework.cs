using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurePluginSetupFramework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SetupDataJson",
                table: "AgentInstallations",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "SetupFlowId",
                table: "AgentInstallations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SetupState",
                table: "AgentInstallations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Ready");

            migrationBuilder.AddColumn<string>(
                name: "SetupStepId",
                table: "AgentInstallations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PluginConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeclarationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProviderProfile = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GrantedScopesJson = table.Column<string>(type: "text", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExternalAccountName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    BoundResourceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PluginConnections_AgentInstallations_AgentInstallationId",
                        column: x => x.AgentInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PluginOAuthAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionDeclarationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScopeSetId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StateHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginOAuthAttempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginConnections_AgentInstallationId_DeclarationId",
                table: "PluginConnections",
                columns: new[] { "AgentInstallationId", "DeclarationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PluginOAuthAttempts_AgentInstallationId_ExpiresAt",
                table: "PluginOAuthAttempts",
                columns: new[] { "AgentInstallationId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PluginOAuthAttempts_StateHash",
                table: "PluginOAuthAttempts",
                column: "StateHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginConnections");

            migrationBuilder.DropTable(
                name: "PluginOAuthAttempts");

            migrationBuilder.DropColumn(
                name: "SetupDataJson",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "SetupFlowId",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "SetupState",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "SetupStepId",
                table: "AgentInstallations");
        }
    }
}
