using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkCollaborationAndSprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SprintId",
                table: "CoreWorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkItemActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorSubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AuthorizingGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorizingGrantRevision = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemActivities_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItemActivities_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItemActivities_ScopedActionGrants_AuthorizingGrantId",
                        column: x => x.AuthorizingGrantId,
                        principalTable: "ScopedActionGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkItemActivities_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AuthorSubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorDisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Body = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemComments_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkItemComments_CoreWorkTasks_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "CoreWorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkSprintMutationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorSubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSprintMutationReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkSprintMutationReceipts_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkSprints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Goal = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSprints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkSprints_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkSprints_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoreWorkTasks_SprintId_BoardRank",
                table: "CoreWorkTasks",
                columns: new[] { "SprintId", "BoardRank" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemActivities_ActorKind_ActorSubjectId_Action_Idempote~",
                table: "WorkItemActivities",
                columns: new[] { "ActorKind", "ActorSubjectId", "Action", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemActivities_AuthorizingGrantId",
                table: "WorkItemActivities",
                column: "AuthorizingGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemActivities_BoardId_OccurredAt",
                table: "WorkItemActivities",
                columns: new[] { "BoardId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemActivities_OrganizationId",
                table: "WorkItemActivities",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemActivities_WorkItemId_OccurredAt",
                table: "WorkItemActivities",
                columns: new[] { "WorkItemId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_AuthorKind_AuthorSubjectId_WorkItemId_Idem~",
                table: "WorkItemComments",
                columns: new[] { "AuthorKind", "AuthorSubjectId", "WorkItemId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_OrganizationId",
                table: "WorkItemComments",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemComments_WorkItemId_CreatedAt",
                table: "WorkItemComments",
                columns: new[] { "WorkItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintMutationReceipts_ActorKind_ActorSubjectId_Action_~",
                table: "WorkSprintMutationReceipts",
                columns: new[] { "ActorKind", "ActorSubjectId", "Action", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprintMutationReceipts_OrganizationId",
                table: "WorkSprintMutationReceipts",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprints_BoardId_Name",
                table: "WorkSprints",
                columns: new[] { "BoardId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprints_BoardId_Status",
                table: "WorkSprints",
                columns: new[] { "BoardId", "Status" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSprints_OrganizationId",
                table: "WorkSprints",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_CoreWorkTasks_WorkSprints_SprintId",
                table: "CoreWorkTasks",
                column: "SprintId",
                principalTable: "WorkSprints",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoreWorkTasks_WorkSprints_SprintId",
                table: "CoreWorkTasks");

            migrationBuilder.DropTable(
                name: "WorkItemActivities");

            migrationBuilder.DropTable(
                name: "WorkItemComments");

            migrationBuilder.DropTable(
                name: "WorkSprintMutationReceipts");

            migrationBuilder.DropTable(
                name: "WorkSprints");

            migrationBuilder.DropIndex(
                name: "IX_CoreWorkTasks_SprintId_BoardRank",
                table: "CoreWorkTasks");

            migrationBuilder.DropColumn(
                name: "SprintId",
                table: "CoreWorkTasks");
        }
    }
}
