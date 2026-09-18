using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskDeliveryReviewsAndMergePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskDeliveryReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpicId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeveloperInstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManagerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    QaInstallationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QualityStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    QualityEvidenceJson = table.Column<string>(type: "text", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedCommitSha = table.Column<string>(type: "text", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "text", nullable: true),
                    Failure = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDeliveryReviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskMergePreferences",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeWorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ManagerOrganizationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskMergePreferences", x => new { x.OrganizationId, x.ScopeWorkItemId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeliveryReviews_OrganizationId_TaskId_PublicationId",
                table: "TaskDeliveryReviews",
                columns: new[] { "OrganizationId", "TaskId", "PublicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeliveryReviews_Status_UpdatedAt",
                table: "TaskDeliveryReviews",
                columns: new[] { "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskDeliveryReviews");

            migrationBuilder.DropTable(
                name: "TaskMergePreferences");
        }
    }
}
