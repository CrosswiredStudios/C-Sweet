using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CSweet.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CSweetDbContext))]
[Migration("20261014190000_RepairFulfilledHiringSuggestions")]
public sealed class RepairFulfilledHiringSuggestions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        WITH completed AS (
            UPDATE "SuggestedUserActions" AS action
            SET "Status" = 'Completed',
                "CompletedAt" = COALESCE(plan."DecidedAt", hire."CreatedAt"),
                "ResultOrganizationUserId" = hire."ResultOrganizationUserId",
                "SupersededAt" = NULL, "SupersededByActionId" = NULL, "SupersededByRole" = NULL
            FROM "WorkforcePlans" AS plan
            JOIN LATERAL (
                SELECT fulfillment."ResultOrganizationUserId", fulfillment."CreatedAt"
                FROM "HiringRecommendationFulfillments" AS fulfillment
                JOIN "CoreOrganizationUsers" AS employee
                  ON employee."Id" = fulfillment."ResultOrganizationUserId"
                 AND employee."OrganizationId" = fulfillment."OrganizationId"
                WHERE fulfillment."WorkforcePlanId" = plan."Id"
                  AND fulfillment."OrganizationId" = plan."OrganizationId"
                ORDER BY fulfillment."CreatedAt" DESC, fulfillment."Id" DESC LIMIT 1
            ) AS hire ON true
            WHERE action."WorkflowType" = 'hiring.marketplace.browse.v1'
              AND action."Status" IN ('Pending', 'Superseded')
              AND action."OrganizationId" = plan."OrganizationId"
              AND plan."Id"::text = lower(action."ParametersJson" ->> 'recommendationId')
              AND plan."Status" = 'Approved'
              AND plan."FulfilledHeadcount" >= plan."Headcount"
            RETURNING action.*
        )
        INSERT INTO "ApplicationRealtimeOutbox"
            ("Id", "OrganizationId", "RecipientOrganizationUserIdsJson", "ChatId", "EventType",
             "Subject", "DataJson", "Status", "Attempts", "NextAttemptAt", "OccurredAt")
        SELECT gen_random_uuid(), action."OrganizationId",
            COALESCE((SELECT jsonb_agg(participant."OrganizationUserId")
                      FROM "ConversationParticipants" AS participant
                      WHERE participant."ConversationId" = action."ConversationId" AND participant."LeftAt" IS NULL), '[]'::jsonb),
            action."ConversationId", 'com.csweet.communication.user-action.completed.v1',
            'organizations/' || action."OrganizationId" || '/communications/chats/' || action."ConversationId" || '/actions/' || action."Id",
            jsonb_build_object('actionId', action."Id", 'messageId', action."ConversationMessageId",
                'workflowType', action."WorkflowType", 'resultOrganizationUserId', action."ResultOrganizationUserId",
                'completedAt', action."CompletedAt"),
            'Pending', 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
        FROM completed AS action;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Completed hires are authoritative; rollback must not recreate actionable cards.
    }
}
