using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifyHiringMarketplaceFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentPlatformEventOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPlatformEventOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SuggestedUserActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginatingInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChatTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    NavigationUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestedUserActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SuggestedUserActions_ChatTurns_ChatTurnId",
                        column: x => x.ChatTurnId,
                        principalTable: "ChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SuggestedUserActions_CoreConversationMessages_ConversationM~",
                        column: x => x.ConversationMessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SuggestedUserActions_CoreConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPlatformEventOutbox_OrganizationId_IdempotencyKey",
                table: "AgentPlatformEventOutbox",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentPlatformEventOutbox_Status_NextAttemptAt",
                table: "AgentPlatformEventOutbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedUserActions_ChatTurnId",
                table: "SuggestedUserActions",
                column: "ChatTurnId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedUserActions_ConversationId",
                table: "SuggestedUserActions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedUserActions_ConversationMessageId",
                table: "SuggestedUserActions",
                column: "ConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedUserActions_OriginatingInstallationId_IdempotencyK~",
                table: "SuggestedUserActions",
                columns: new[] { "OriginatingInstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPlatformEventOutbox");

            migrationBuilder.DropTable(
                name: "SuggestedUserActions");
        }
    }
}
