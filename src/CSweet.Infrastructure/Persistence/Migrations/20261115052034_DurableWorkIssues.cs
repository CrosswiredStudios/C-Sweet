using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableWorkIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DurableMutationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    MutationKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ContentDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DurableMutationReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkIssueCases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObligationKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Episode = table.Column<int>(type: "integer", nullable: false),
                    PreviousIssueId = table.Column<Guid>(type: "uuid", nullable: true),
                    Statement = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    BriefTitle = table.Column<string>(type: "text", nullable: false),
                    BriefSummary = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    AccountableEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    NextAction = table.Column<string>(type: "text", nullable: false),
                    SourceJson = table.Column<string>(type: "text", nullable: false),
                    ScopeKeysJson = table.Column<string>(type: "text", nullable: false),
                    LinksJson = table.Column<string>(type: "text", nullable: false),
                    ResolutionEvidence = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkIssueCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkIssueCommissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitiatorInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueIdsJson = table.Column<string>(type: "text", nullable: false),
                    SourceJson = table.Column<string>(type: "text", nullable: false),
                    Question = table.Column<string>(type: "text", nullable: false),
                    SupersededSessionIdsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Failure = table.Column<string>(type: "text", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkIssueCommissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DurableMutationReceipts_OrganizationId_PrincipalId_Resource~",
                table: "DurableMutationReceipts",
                columns: new[] { "OrganizationId", "PrincipalId", "ResourceKey", "MutationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkIssueCases_OrganizationId_BoardId_ObligationKey_Episode",
                table: "WorkIssueCases",
                columns: new[] { "OrganizationId", "BoardId", "ObligationKey", "Episode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkIssueCases_OrganizationId_Status_UpdatedAt",
                table: "WorkIssueCases",
                columns: new[] { "OrganizationId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkIssueCommissions_Status_NextAttemptAt",
                table: "WorkIssueCommissions",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DurableMutationReceipts");

            migrationBuilder.DropTable(
                name: "WorkIssueCases");

            migrationBuilder.DropTable(
                name: "WorkIssueCommissions");
        }
    }
}
