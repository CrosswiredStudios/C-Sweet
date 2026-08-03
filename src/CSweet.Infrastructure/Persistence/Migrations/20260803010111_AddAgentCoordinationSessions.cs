using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentCoordinationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CoordinationSessionId",
                table: "CoreConversationMessages",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentCoordinationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChatTurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentAgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Objective = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    SuccessCriteriaJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    NextTurnOrdinal = table.Column<int>(type: "integer", nullable: false),
                    IsFinalization = table.Column<bool>(type: "boolean", nullable: false),
                    FinalSummary = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCoordinationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_AgentInstallations_InitiatorInsta~",
                        column: x => x.InitiatorInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_AgentInstallations_TargetInstalla~",
                        column: x => x.TargetInstallationId,
                        principalTable: "AgentInstallations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_AgentWorkItems_CurrentAgentWorkIt~",
                        column: x => x.CurrentAgentWorkItemId,
                        principalTable: "AgentWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_ChatTurns_SourceChatTurnId",
                        column: x => x.SourceChatTurnId,
                        principalTable: "ChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreConversationMessages_SourceMe~",
                        column: x => x.SourceMessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreConversations_SourceConversat~",
                        column: x => x.SourceConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreOrganizationUsers_CurrentOrga~",
                        column: x => x.CurrentOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreOrganizationUsers_InitiatorOr~",
                        column: x => x.InitiatorOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreOrganizationUsers_TargetOrgan~",
                        column: x => x.TargetOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationSessions_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentCoordinationTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpeakerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Content = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCoordinationTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationTurns_AgentCoordinationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AgentCoordinationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationTurns_AgentWorkItems_AgentWorkItemId",
                        column: x => x.AgentWorkItemId,
                        principalTable: "AgentWorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationTurns_CoreConversationMessages_Conversatio~",
                        column: x => x.ConversationMessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentCoordinationTurns_CoreOrganizationUsers_SpeakerOrganiz~",
                        column: x => x.SpeakerOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoreConversationMessages_CoordinationSessionId",
                table: "CoreConversationMessages",
                column: "CoordinationSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_ConversationId",
                table: "AgentCoordinationSessions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_CurrentAgentWorkItemId",
                table: "AgentCoordinationSessions",
                column: "CurrentAgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_CurrentOrganizationUserId",
                table: "AgentCoordinationSessions",
                column: "CurrentOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_InitiatorInstallationId",
                table: "AgentCoordinationSessions",
                column: "InitiatorInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_InitiatorOrganizationUserId",
                table: "AgentCoordinationSessions",
                column: "InitiatorOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_OrganizationId_ConversationId_Sta~",
                table: "AgentCoordinationSessions",
                columns: new[] { "OrganizationId", "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_OrganizationId_IdempotencyKey",
                table: "AgentCoordinationSessions",
                columns: new[] { "OrganizationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_OrganizationId_SourceConversation~",
                table: "AgentCoordinationSessions",
                columns: new[] { "OrganizationId", "SourceConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_SourceChatTurnId",
                table: "AgentCoordinationSessions",
                column: "SourceChatTurnId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_SourceConversationId",
                table: "AgentCoordinationSessions",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_SourceMessageId",
                table: "AgentCoordinationSessions",
                column: "SourceMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_TargetInstallationId",
                table: "AgentCoordinationSessions",
                column: "TargetInstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationSessions_TargetOrganizationUserId",
                table: "AgentCoordinationSessions",
                column: "TargetOrganizationUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_AgentWorkItemId",
                table: "AgentCoordinationTurns",
                column: "AgentWorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_ConversationMessageId",
                table: "AgentCoordinationTurns",
                column: "ConversationMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_EventId",
                table: "AgentCoordinationTurns",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_SessionId_IdempotencyKey",
                table: "AgentCoordinationTurns",
                columns: new[] { "SessionId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_SessionId_Ordinal",
                table: "AgentCoordinationTurns",
                columns: new[] { "SessionId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCoordinationTurns_SpeakerOrganizationUserId",
                table: "AgentCoordinationTurns",
                column: "SpeakerOrganizationUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCoordinationTurns");

            migrationBuilder.DropTable(
                name: "AgentCoordinationSessions");

            migrationBuilder.DropIndex(
                name: "IX_CoreConversationMessages_CoordinationSessionId",
                table: "CoreConversationMessages");

            migrationBuilder.DropColumn(
                name: "CoordinationSessionId",
                table: "CoreConversationMessages");
        }
    }
}
