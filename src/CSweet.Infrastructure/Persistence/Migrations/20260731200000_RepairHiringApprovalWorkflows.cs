using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20260731200000_RepairHiringApprovalWorkflows")]
public sealed class RepairHiringApprovalWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "ChatTurnId", table: "StaffingActionProposals", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "ConversationId", table: "StaffingActionProposals", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "ConversationMessageId", table: "StaffingActionProposals", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "DecidedByOrganizationUserId", table: "StaffingActionProposals", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "DecisionComment", table: "StaffingActionProposals", type: "character varying(2048)", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "SubmittedAt", table: "StaffingActionProposals", type: "timestamp with time zone", nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE "StaffingActionProposals"
            SET "SubmittedAt" = "CreatedAt"
            WHERE "RequestingInstallationId" <> '00000000-0000-0000-0000-000000000000'
              AND "SubmittedAt" IS NULL;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_StaffingActionProposals_ConversationMessageId",
            table: "StaffingActionProposals",
            column: "ConversationMessageId",
            unique: true,
            filter: "\"ConversationMessageId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_StaffingActionProposals_ConversationMessageId", table: "StaffingActionProposals");
        migrationBuilder.DropColumn(name: "ChatTurnId", table: "StaffingActionProposals");
        migrationBuilder.DropColumn(name: "ConversationId", table: "StaffingActionProposals");
        migrationBuilder.DropColumn(name: "ConversationMessageId", table: "StaffingActionProposals");
        migrationBuilder.DropColumn(name: "DecidedByOrganizationUserId", table: "StaffingActionProposals");
        migrationBuilder.DropColumn(name: "DecisionComment", table: "StaffingActionProposals");
        migrationBuilder.DropColumn(name: "SubmittedAt", table: "StaffingActionProposals");
    }
}
