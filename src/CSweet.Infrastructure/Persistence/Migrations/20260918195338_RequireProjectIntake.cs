using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireProjectIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceIntakeId",
                table: "AgentCoordinationSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LegacyDevelopmentAuthorizations",
                columns: table => new
                {
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyDevelopmentAuthorizations", x => x.WorkItemId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDeliveryBindings",
                columns: table => new
                {
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDeliveryBindings", x => x.WorkstreamId);
                    table.ForeignKey(
                        name: "FK_ProjectDeliveryBindings_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectIntakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestingHumanId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeveloperId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeveloperInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChatTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: true),
                    RootItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    ManagerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChiefId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoordinationSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    HiringRecommendationId = table.Column<Guid>(type: "uuid", nullable: true),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalRequest = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Goal = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: false),
                    TicketOwner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Issue = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastChoiceMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastChoiceKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIntakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectManagerReservations",
                columns: table => new
                {
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntakeId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectManagerReservations", x => x.OrganizationUserId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectParticipants",
                columns: table => new
                {
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectParticipants", x => new { x.WorkstreamId, x.OrganizationUserId });
                    table.ForeignKey(
                        name: "FK_ProjectParticipants_CoreOrganizationUsers_OrganizationUserId",
                        column: x => x.OrganizationUserId,
                        principalTable: "CoreOrganizationUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectParticipants_Workstreams_WorkstreamId",
                        column: x => x.WorkstreamId,
                        principalTable: "Workstreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDeliveryBindings_BoardId",
                table: "ProjectDeliveryBindings",
                column: "BoardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDeliveryBindings_OrganizationId_CreationKey",
                table: "ProjectDeliveryBindings",
                columns: new[] { "OrganizationId", "CreationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIntakes_DeveloperInstallationId_Status_UpdatedAt",
                table: "ProjectIntakes",
                columns: new[] { "DeveloperInstallationId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIntakes_OrganizationId_DeveloperInstallationId_Idemp~",
                table: "ProjectIntakes",
                columns: new[] { "OrganizationId", "DeveloperInstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectParticipants_OrganizationId_OrganizationUserId_Remov~",
                table: "ProjectParticipants",
                columns: new[] { "OrganizationId", "OrganizationUserId", "RemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectParticipants_OrganizationUserId",
                table: "ProjectParticipants",
                column: "OrganizationUserId");
            // Immutable built-in profile content pinned to this migration.
            migrationBuilder.InsertData("WorkstreamProfileDefinitions",
                new[] { "Id", "Key", "Version", "DisplayName", "MetadataSchemaJson", "LifecyclePolicyKey", "DefaultBoardProfileKey", "Status", "ProviderPackageId", "ProviderPackageVersion", "DefinitionDigest", "DefinitionJson", "CreatedAt" },
                new object[] { Guid.Parse("01a5aeed-8b71-4b9d-8ac2-0a68b31720e5"), "software-prototype.v1", 1, "Software prototype",
                    """{"type":"object","properties":{"intakeId":{"type":"string"},"setupChoiceMessageId":{"type":"string"},"participantIds":{"type":"array","items":{"type":"string"}}},"additionalProperties":false}""", "software-prototype.lifecycle.v1", "general-work.v1", "Active", "com.csweet.platform", "1.0.0",
                    "5377fbe74d83f7238ca4a05bdcb04f6df16c2d51d88620d6a42417a5864efd2c", """{"key":"software-prototype.v1","version":1,"displayName":"Software prototype","lifecyclePolicyKey":"software-prototype.lifecycle.v1","defaultBoardProfileKey":"general-work.v1","metadataSchema":{"type":"object","properties":{"intakeId":{"type":"string"},"setupChoiceMessageId":{"type":"string"},"participantIds":{"type":"array","items":{"type":"string"}}},"additionalProperties":false},"lifecycle":{"stages":[{"key":"Development"},{"key":"Testing"},{"key":"Completed"},{"key":"Cancelled"}],"transitions":[{"from":"Development","to":"Testing"},{"from":"Testing","to":"Development"},{"from":"Testing","to":"Completed"},{"from":"Development","to":"Cancelled"},{"from":"Testing","to":"Cancelled"},{"from":"Completed","to":"Development"},{"from":"Cancelled","to":"Development"}]}}""", new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero) });
            // Capture execution evidence exactly once at rollout. Ready/queued assignments alone do not qualify.
            migrationBuilder.Sql("""
                INSERT INTO "LegacyDevelopmentAuthorizations" ("WorkItemId", "OrganizationId")
                SELECT t."Id", t."OrganizationId" FROM "CoreWorkTasks" t
                WHERE t."ArchivedAt" IS NULL AND t."Status" NOT IN ('Completed', 'Cancelled') AND (
                    t."ClaimEventId" IS NOT NULL OR
                    EXISTS (SELECT 1 FROM "SourceControlWorkspaces" w WHERE w."WorkItemId" = t."Id") OR
                    EXISTS (SELECT 1 FROM "WorkItemExecutions" e JOIN "WorkStageExecutions" s ON s."ItemExecutionId" = e."Id"
                        JOIN "WorkExecutionAttempts" a ON a."StageExecutionId" = s."Id" WHERE e."WorkItemId" = t."Id" AND a."StartedAt" IS NOT NULL)
                ) ON CONFLICT DO NOTHING;
                -- Existing descendants of a started request are part of that request, not new requests.
                WITH RECURSIVE started("Id", "OrganizationId") AS (
                    SELECT "WorkItemId", "OrganizationId" FROM "LegacyDevelopmentAuthorizations"
                    UNION
                    SELECT t."Id", t."OrganizationId" FROM "CoreWorkTasks" t JOIN started p ON t."ParentWorkTaskId" = p."Id"
                    WHERE t."ArchivedAt" IS NULL AND t."OrganizationId" = p."OrganizationId"
                ) INSERT INTO "LegacyDevelopmentAuthorizations" ("WorkItemId", "OrganizationId")
                SELECT "Id", "OrganizationId" FROM started ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData("WorkstreamProfileDefinitions", "Id", Guid.Parse("01a5aeed-8b71-4b9d-8ac2-0a68b31720e5"));
            migrationBuilder.DropTable(
                name: "LegacyDevelopmentAuthorizations");

            migrationBuilder.DropTable(
                name: "ProjectDeliveryBindings");

            migrationBuilder.DropTable(
                name: "ProjectIntakes");

            migrationBuilder.DropTable(
                name: "ProjectManagerReservations");

            migrationBuilder.DropTable(
                name: "ProjectParticipants");

            migrationBuilder.DropColumn(
                name: "SourceIntakeId",
                table: "AgentCoordinationSessions");
        }
    }
}
