using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonalAgentTodosAndStructuredMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPersonalTodo",
                table: "WorkBoards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByOrganizationUserId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalTodoBlockReason",
                table: "CoreWorkTasks",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PersonalTodoClaimEventId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PersonalTodoClaimExpiresAt",
                table: "CoreWorkTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalTodoIdempotencyKey",
                table: "CoreWorkTasks",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalTodoResultSummary",
                table: "CoreWorkTasks",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceConversationId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceMessageId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationMessageMentions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MentionedOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Offset = table.Column<int>(type: "integer", nullable: false),
                    Length = table.Column<int>(type: "integer", nullable: false),
                    DisplayText = table.Column<string>(type: "character varying(161)", maxLength: 161, nullable: false),
                    RecipientWasParticipant = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessageMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMessageMentions_CoreConversationMessages_Messag~",
                        column: x => x.MessageId,
                        principalTable: "CoreConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessageMentions_CoreConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "CoreConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ConversationMessageMentions_CoreOrganizationUsers_Mentioned~",
                        column: x => x.MentionedOrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkBoards_PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards",
                column: "PersonalTodoOwnerOrganizationUserId",
                unique: true,
                filter: "\"PersonalTodoOwnerOrganizationUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_CreatedByOrganizationUserId_PersonalTodoIdemp~",
                table: "CoreWorkTasks",
                columns: new[] { "CreatedByOrganizationUserId", "PersonalTodoIdempotencyKey" },
                unique: true,
                filter: "\"PersonalTodoIdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_SourceConversationId",
                table: "CoreWorkTasks",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_SourceMessageId",
                table: "CoreWorkTasks",
                column: "SourceMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageMentions_ConversationId",
                table: "ConversationMessageMentions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageMentions_MentionedOrganizationUserId_Cre~",
                table: "ConversationMessageMentions",
                columns: new[] { "MentionedOrganizationUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessageMentions_MessageId_MentionedOrganization~",
                table: "ConversationMessageMentions",
                columns: new[] { "MessageId", "MentionedOrganizationUserId", "Offset" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_CoreConversationMessages_SourceMessageId",
                table: "CoreWorkTasks",
                column: "SourceMessageId",
                principalTable: "CoreConversationMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_CoreConversations_SourceConversationId",
                table: "CoreWorkTasks",
                column: "SourceConversationId",
                principalTable: "CoreConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_CoreOrganizationUsers_CreatedByOrganizationUs~",
                table: "CoreWorkTasks",
                column: "CreatedByOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_PersonalTodoOwnerOrganizat~",
                table: "WorkBoards",
                column: "PersonalTodoOwnerOrganizationUserId",
                principalTable: "CoreOrganizationUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                WITH inserted AS (
                    INSERT INTO "WorkBoards" (
                        "Id", "OrganizationId", "TeamId", "WorkstreamId",
                        "ManagerOrganizationUserId", "PersonalTodoOwnerOrganizationUserId",
                        "IsPersonalTodo", "Key", "NextItemSequence", "Name", "Description",
                        "IsDefault", "Revision", "CreatedAt", "UpdatedAt", "ArchivedAt")
                    SELECT gen_random_uuid(), u."OrganizationId", NULL, NULL,
                           u."ReportsToOrganizationUserId", u."Id", TRUE,
                           UPPER('TD' || LEFT(REPLACE(u."Id"::text, '-', ''), 10)), 1,
                           LEFT(u."DisplayName", 152) || '''s To Do',
                           'Protected personal work queue.', FALSE, 1, NOW(), NOW(), NULL
                    FROM "CoreOrganizationUsers" u
                    WHERE u."IsActive" = TRUE
                      AND u."EmployeeType" = 'Agent'
                      AND u."AgentInstallationId" IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM "WorkBoards" b
                          WHERE b."PersonalTodoOwnerOrganizationUserId" = u."Id")
                    RETURNING "Id"
                )
                INSERT INTO "WorkBoardColumns" (
                    "Id", "BoardId", "Name", "Category", "Position", "WipPolicy", "WipLimit")
                SELECT gen_random_uuid(), inserted."Id", columns."Name", columns."Category",
                       columns."Position", 'Disabled', NULL
                FROM inserted
                CROSS JOIN (VALUES
                    ('To Do', 'ToDo', 0),
                    ('Doing', 'InProgress', 1),
                    ('Done', 'Done', 2)
                ) AS columns("Name", "Category", "Position");

                INSERT INTO "ScopedActionGrants" (
                    "Id", "OrganizationId", "SubjectKind", "SubjectId", "Action",
                    "ScopeKind", "ScopeId", "CanDelegate", "ParentGrantId",
                    "GrantedBySubjectKind", "GrantedBySubjectId", "Revision",
                    "GrantedAt", "ExpiresAt", "RevokedAt")
                SELECT gen_random_uuid(), b."OrganizationId", 'AgentInstallation',
                       u."AgentInstallationId", actions."Action", 'Board', b."Id", FALSE, NULL,
                       'OrganizationUser', u."Id", 1, NOW(), NULL, NULL
                FROM "WorkBoards" b
                JOIN "CoreOrganizationUsers" u
                  ON u."Id" = b."PersonalTodoOwnerOrganizationUserId"
                CROSS JOIN (VALUES
                    ('work.personal-todo.read.v1'),
                    ('work.personal-todo.add.v1'),
                    ('work.personal-todo.requeue.v1'),
                    ('work.personal-todo.claim.v1'),
                    ('work.personal-todo.complete.v1'),
                    ('work.personal-todo.block.v1'),
                    ('work.personal-todo.release.v1')
                ) AS actions("Action")
                WHERE b."IsPersonalTodo" = TRUE
                  AND u."IsActive" = TRUE
                  AND u."AgentInstallationId" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "ScopedActionGrants" g
                      WHERE g."OrganizationId" = b."OrganizationId"
                        AND g."SubjectKind" = 'AgentInstallation'
                        AND g."SubjectId" = u."AgentInstallationId"
                        AND g."Action" = actions."Action"
                        AND g."ScopeKind" = 'Board'
                        AND g."ScopeId" = b."Id"
                        AND g."RevokedAt" IS NULL);

                INSERT INTO "ScopedActionGrants" (
                    "Id", "OrganizationId", "SubjectKind", "SubjectId", "Action",
                    "ScopeKind", "ScopeId", "CanDelegate", "ParentGrantId",
                    "GrantedBySubjectKind", "GrantedBySubjectId", "Revision",
                    "GrantedAt", "ExpiresAt", "RevokedAt")
                SELECT gen_random_uuid(), b."OrganizationId", 'OrganizationUser',
                       u."ReportsToOrganizationUserId", actions."Action", 'Board', b."Id", FALSE, NULL,
                       'OrganizationUser', u."Id", 1, NOW(), NULL, NULL
                FROM "WorkBoards" b
                JOIN "CoreOrganizationUsers" u
                  ON u."Id" = b."PersonalTodoOwnerOrganizationUserId"
                JOIN "CoreOrganizationUsers" manager
                  ON manager."Id" = u."ReportsToOrganizationUserId" AND manager."IsActive" = TRUE
                CROSS JOIN (VALUES
                    ('work.personal-todo.read.v1'),
                    ('work.personal-todo.add.v1'),
                    ('work.personal-todo.reorder.v1'),
                    ('work.personal-todo.requeue.v1')
                ) AS actions("Action")
                WHERE b."IsPersonalTodo" = TRUE
                  AND NOT EXISTS (
                      SELECT 1 FROM "ScopedActionGrants" g
                      WHERE g."OrganizationId" = b."OrganizationId"
                        AND g."SubjectKind" = 'OrganizationUser'
                        AND g."SubjectId" = u."ReportsToOrganizationUserId"
                        AND g."Action" = actions."Action"
                        AND g."ScopeKind" = 'Board'
                        AND g."ScopeId" = b."Id"
                        AND g."RevokedAt" IS NULL);

                INSERT INTO "ScopedActionGrants" (
                    "Id", "OrganizationId", "SubjectKind", "SubjectId", "Action",
                    "ScopeKind", "ScopeId", "CanDelegate", "ParentGrantId",
                    "GrantedBySubjectKind", "GrantedBySubjectId", "Revision",
                    "GrantedAt", "ExpiresAt", "RevokedAt")
                SELECT gen_random_uuid(), b."OrganizationId", 'AgentInstallation',
                       manager."AgentInstallationId", actions."Action", 'Board', b."Id", FALSE, NULL,
                       'OrganizationUser', u."Id", 1, NOW(), NULL, NULL
                FROM "WorkBoards" b
                JOIN "CoreOrganizationUsers" u
                  ON u."Id" = b."PersonalTodoOwnerOrganizationUserId"
                JOIN "CoreOrganizationUsers" manager
                  ON manager."Id" = u."ReportsToOrganizationUserId"
                 AND manager."IsActive" = TRUE
                 AND manager."AgentInstallationId" IS NOT NULL
                CROSS JOIN (VALUES
                    ('work.personal-todo.read.v1'),
                    ('work.personal-todo.add.v1'),
                    ('work.personal-todo.reorder.v1'),
                    ('work.personal-todo.requeue.v1')
                ) AS actions("Action")
                WHERE b."IsPersonalTodo" = TRUE
                  AND NOT EXISTS (
                      SELECT 1 FROM "ScopedActionGrants" g
                      WHERE g."OrganizationId" = b."OrganizationId"
                        AND g."SubjectKind" = 'AgentInstallation'
                        AND g."SubjectId" = manager."AgentInstallationId"
                        AND g."Action" = actions."Action"
                        AND g."ScopeKind" = 'Board'
                        AND g."ScopeId" = b."Id"
                        AND g."RevokedAt" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_CoreConversationMessages_SourceMessageId",
                table: "CoreWorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_CoreConversations_SourceConversationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_CoreOrganizationUsers_CreatedByOrganizationUs~",
                table: "CoreWorkTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkBoards_CoreOrganizationUsers_PersonalTodoOwnerOrganizat~",
                table: "WorkBoards");

            migrationBuilder.DropTable(
                name: "ConversationMessageMentions");

            migrationBuilder.DropIndex(
                name: "IX_WorkBoards_PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_CreatedByOrganizationUserId_PersonalTodoIdemp~",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_SourceConversationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_SourceMessageId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "IsPersonalTodo",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "PersonalTodoOwnerOrganizationUserId",
                table: "WorkBoards");

            migrationBuilder.DropColumn(
                name: "CreatedByOrganizationUserId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "PersonalTodoBlockReason",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "PersonalTodoClaimEventId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "PersonalTodoClaimExpiresAt",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "PersonalTodoIdempotencyKey",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "PersonalTodoResultSummary",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "SourceConversationId",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "SourceMessageId",
                table: "CoreWorkTasks");
        }
    }
}
