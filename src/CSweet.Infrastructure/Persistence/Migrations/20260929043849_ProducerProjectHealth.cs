using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProducerProjectHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectHealthSignals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKind = table.Column<string>(type: "text", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MeaningfulProgress = table.Column<bool>(type: "boolean", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectHealthSignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectHealthStates",
                columns: table => new
                {
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProducerEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextReviewAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HasActiveWork = table.Column<bool>(type: "boolean", nullable: false),
                    WaitingReason = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectHealthStates", x => x.WorkstreamId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectIncidentDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Markdown = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIncidentDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectIncidentReceipts",
                columns: table => new
                {
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "text", nullable: false),
                    RequestHash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIncidentReceipts", x => new { x.IncidentId, x.ActorId, x.IdempotencyKey });
                });

            migrationBuilder.CreateTable(
                name: "ProjectIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkstreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProducerEmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentRecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    AffectedWorkItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Facts = table.Column<string>(type: "text", nullable: false),
                    LikelyCause = table.Column<string>(type: "text", nullable: false),
                    MissingEvidence = table.Column<string>(type: "text", nullable: false),
                    RecommendedAction = table.Column<string>(type: "text", nullable: false),
                    EvidenceJson = table.Column<string>(type: "text", nullable: false),
                    HistoryJson = table.Column<string>(type: "text", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastProgressAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EscalateAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIncidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectHealthSignals_ProcessedAt_OccurredAt",
                table: "ProjectHealthSignals",
                columns: new[] { "ProcessedAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectHealthSignals_WorkstreamId_OccurredAt",
                table: "ProjectHealthSignals",
                columns: new[] { "WorkstreamId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectHealthStates_NextReviewAt",
                table: "ProjectHealthStates",
                column: "NextReviewAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIncidentDeliveries_DeliveredAt_NextAttemptAt",
                table: "ProjectIncidentDeliveries",
                columns: new[] { "DeliveredAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIncidentDeliveries_IncidentId_RecipientId_Revision",
                table: "ProjectIncidentDeliveries",
                columns: new[] { "IncidentId", "RecipientId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIncidents_OrganizationId_CurrentRecipientId_Detected~",
                table: "ProjectIncidents",
                columns: new[] { "OrganizationId", "CurrentRecipientId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIncidents_OrganizationId_WorkstreamId_Fingerprint",
                table: "ProjectIncidents",
                columns: new[] { "OrganizationId", "WorkstreamId", "Fingerprint" },
                unique: true,
                filter: "\"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIncidents_Status_EscalateAt",
                table: "ProjectIncidents",
                columns: new[] { "Status", "EscalateAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectHealthSignals");

            migrationBuilder.DropTable(
                name: "ProjectHealthStates");

            migrationBuilder.DropTable(
                name: "ProjectIncidentDeliveries");

            migrationBuilder.DropTable(
                name: "ProjectIncidentReceipts");

            migrationBuilder.DropTable(
                name: "ProjectIncidents");
        }
    }
}
