using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureWorkAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkAutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AutomationIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TriggerEventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ConditionColumnId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TargetColumnId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAutomationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_WorkBoardColumns_ConditionColumnId",
                        column: x => x.ConditionColumnId,
                        principalTable: "WorkBoardColumns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_WorkBoardColumns_TargetColumnId",
                        column: x => x.TargetColumnId,
                        principalTable: "WorkBoardColumns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationRules_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkAutomationExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RequiredAction = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AuthorizingGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorizingGrantRevision = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAutomationExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_CoreOrganizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "CoreOrganizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_ScopedActionGrants_AuthorizingGran~",
                        column: x => x.AuthorizingGrantId,
                        principalTable: "ScopedActionGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_WorkAutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "WorkAutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_WorkBoards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "WorkBoards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkAutomationExecutions_WorkItemActivities_SourceActivityId",
                        column: x => x.SourceActivityId,
                        principalTable: "WorkItemActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemActivities_BoardId_EventType_OccurredAt",
                table: "WorkItemActivities",
                columns: new[] { "BoardId", "EventType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_AuthorizingGrantId",
                table: "WorkAutomationExecutions",
                column: "AuthorizingGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_BoardId_CompletedAt",
                table: "WorkAutomationExecutions",
                columns: new[] { "BoardId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_OrganizationId",
                table: "WorkAutomationExecutions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_RuleId_SourceActivityId",
                table: "WorkAutomationExecutions",
                columns: new[] { "RuleId", "SourceActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationExecutions_SourceActivityId",
                table: "WorkAutomationExecutions",
                column: "SourceActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_AutomationIdentityId",
                table: "WorkAutomationRules",
                column: "AutomationIdentityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_BoardId_IsEnabled",
                table: "WorkAutomationRules",
                columns: new[] { "BoardId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_ConditionColumnId",
                table: "WorkAutomationRules",
                column: "ConditionColumnId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_OrganizationId",
                table: "WorkAutomationRules",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAutomationRules_TargetColumnId",
                table: "WorkAutomationRules",
                column: "TargetColumnId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkAutomationExecutions");

            migrationBuilder.DropTable(
                name: "WorkAutomationRules");

            migrationBuilder.DropIndex(
                name: "IX_WorkItemActivities_BoardId_EventType_OccurredAt",
                table: "WorkItemActivities");
        }
    }
}
