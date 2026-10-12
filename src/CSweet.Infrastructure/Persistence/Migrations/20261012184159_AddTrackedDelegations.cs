using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedDelegations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedDelegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalRequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoordinatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceChecksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportingConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutionTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExecutionMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CommitmentItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentDelegationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Instruction = table.Column<string>(type: "text", nullable: false),
                    IntendedOutcome = table.Column<string>(type: "text", nullable: false),
                    InterestedPartiesJson = table.Column<string>(type: "text", nullable: false),
                    WorkContextJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IsDirective = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttentionRaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedDelegations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkTrackingOperationReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdateId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTrackingOperationReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkTrackingSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    FollowerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnfollowedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessedThrough = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTrackingSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkTrackingUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportMessageId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTrackingUpdates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedDelegations_OrganizationId_CoordinatorId_Idempotency~",
                table: "TrackedDelegations",
                columns: new[] { "OrganizationId", "CoordinatorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedDelegations_OrganizationId_RecipientId_Status",
                table: "TrackedDelegations",
                columns: new[] { "OrganizationId", "RecipientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedDelegations_OrganizationId_WorkItemId",
                table: "TrackedDelegations",
                columns: new[] { "OrganizationId", "WorkItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingOperationReceipts_OrganizationId_ActorId_Operat~",
                table: "WorkTrackingOperationReceipts",
                columns: new[] { "OrganizationId", "ActorId", "Operation", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingOperationReceipts_OrganizationId_ActorId_Update~",
                table: "WorkTrackingOperationReceipts",
                columns: new[] { "OrganizationId", "ActorId", "UpdateId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingSubscriptions_OrganizationId_FollowerId_Unfollo~",
                table: "WorkTrackingSubscriptions",
                columns: new[] { "OrganizationId", "FollowerId", "UnfollowedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingSubscriptions_OrganizationId_WorkItemId_Followe~",
                table: "WorkTrackingSubscriptions",
                columns: new[] { "OrganizationId", "WorkItemId", "FollowerId", "DelegationId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingUpdates_OrganizationId_AuthorId_IdempotencyKey",
                table: "WorkTrackingUpdates",
                columns: new[] { "OrganizationId", "AuthorId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingUpdates_OrganizationId_DelegationId_ReportedAt_~",
                table: "WorkTrackingUpdates",
                columns: new[] { "OrganizationId", "DelegationId", "ReportedAt", "AvailableAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTrackingUpdates_OrganizationId_WorkItemId_OccurredAt_Id",
                table: "WorkTrackingUpdates",
                columns: new[] { "OrganizationId", "WorkItemId", "OccurredAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackedDelegations");

            migrationBuilder.DropTable(
                name: "WorkTrackingOperationReceipts");

            migrationBuilder.DropTable(
                name: "WorkTrackingSubscriptions");

            migrationBuilder.DropTable(
                name: "WorkTrackingUpdates");
        }
    }
}
