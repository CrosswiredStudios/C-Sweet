using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChiefLeadershipState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChiefCoverageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    DomainId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CoverageStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExecutionStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AccountableOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoverageModel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Rationale = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    NextAction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RecommendationId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnswerDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BlockCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    AcceptedResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReviewAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefCoverageRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChiefFocusPrompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    ExpectedFocusRevision = table.Column<long>(type: "bigint", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefFocusPrompts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChiefFocusRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    DomainId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CustomText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecisionMakerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersedesId = table.Column<Guid>(type: "uuid", nullable: true),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefFocusRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChiefLeadershipAgendas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfileKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProfileVersion = table.Column<int>(type: "integer", nullable: false),
                    FocusRevision = table.Column<long>(type: "bigint", nullable: false),
                    FocusId = table.Column<Guid>(type: "uuid", nullable: true),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefLeadershipAgendas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChiefLeadershipReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChiefLeadershipReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChiefCoverageRecords_OrganizationId_ExecutionStatus_Id",
                table: "ChiefCoverageRecords",
                columns: new[] { "OrganizationId", "ExecutionStatus", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ChiefCoverageRecords_OrganizationId_ProfileKey_ProfileVersi~",
                table: "ChiefCoverageRecords",
                columns: new[] { "OrganizationId", "ProfileKey", "ProfileVersion", "DomainId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChiefFocusPrompts_InstallationId_IdempotencyKey",
                table: "ChiefFocusPrompts",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChiefFocusRecords_OrganizationId_Revision",
                table: "ChiefFocusRecords",
                columns: new[] { "OrganizationId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChiefFocusRecords_OrganizationId_SourceDecisionId",
                table: "ChiefFocusRecords",
                columns: new[] { "OrganizationId", "SourceDecisionId" },
                unique: true,
                filter: "\"SourceDecisionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChiefLeadershipReceipts_OrganizationId_InstallationId_Opera~",
                table: "ChiefLeadershipReceipts",
                columns: new[] { "OrganizationId", "InstallationId", "OperationKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback uses the pause control. Never destroy retained decisions, outcomes
            // or operation receipts merely to run an older agent.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM "ChiefFocusRecords") OR
                     EXISTS (SELECT 1 FROM "ChiefCoverageRecords") OR
                     EXISTS (SELECT 1 FROM "ChiefFocusPrompts") OR
                     EXISTS (SELECT 1 FROM "ChiefLeadershipReceipts") THEN
                    RAISE EXCEPTION 'Chief leadership history exists. Pause execution and retain the additive schema.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "ChiefCoverageRecords");

            migrationBuilder.DropTable(
                name: "ChiefFocusPrompts");

            migrationBuilder.DropTable(
                name: "ChiefFocusRecords");

            migrationBuilder.DropTable(
                name: "ChiefLeadershipAgendas");

            migrationBuilder.DropTable(
                name: "ChiefLeadershipReceipts");
        }
    }
}
