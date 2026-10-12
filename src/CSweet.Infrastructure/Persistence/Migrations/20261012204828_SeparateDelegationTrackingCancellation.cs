using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

/// <summary>Reporting cancellation preserves the recipient's outstanding execution request.</summary>
public partial class SeparateDelegationTrackingCancellation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(name: "TrackingCancelledAt", table: "TrackedDelegations",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.Sql("""
            UPDATE "TrackedDelegations" SET "TrackingCancelledAt" = "UpdatedAt",
                "Status" = CASE WHEN "ExecutionTurnId" IS NULL THEN 'AwaitingAcknowledgement' ELSE 'Active' END
            WHERE "Status" = 'Cancelled';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "TrackedDelegations" SET "Status" = 'Cancelled' WHERE "TrackingCancelledAt" IS NOT NULL;
            """);
        migrationBuilder.DropColumn(name: "TrackingCancelledAt", table: "TrackedDelegations");
    }
}
