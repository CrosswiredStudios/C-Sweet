using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Data-only repair. Roles suggested together for one source message or chat turn are a single
    /// multi-role hiring batch, but UserActionService compared ids that materialization rewrites, so each
    /// role superseded the previous one. Restore those same-batch supersessions to Pending when the
    /// role's hiring recommendation is still pending. No schema change.
    /// </summary>
    public partial class RestoreSameBatchHiringSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "SuggestedUserActions" AS action
                SET "Status" = 'Pending',
                    "SupersededAt" = NULL,
                    "SupersededByActionId" = NULL,
                    "SupersededByRole" = NULL
                FROM "SuggestedUserActions" AS replacement,
                     "CoreConversationMessages" AS action_message,
                     "CoreConversationMessages" AS replacement_message,
                     "WorkforcePlans" AS plan
                WHERE action."Status" = 'Superseded'
                  AND action."WorkflowType" = 'hiring.marketplace.browse.v1'
                  AND replacement."Id" = action."SupersededByActionId"
                  AND action_message."Id" = action."ConversationMessageId"
                  AND replacement_message."Id" = replacement."ConversationMessageId"
                  AND action_message."CausationId" IS NOT NULL
                  AND action_message."CausationId" = replacement_message."CausationId"
                  AND plan."Id"::text = lower(action."ParametersJson" ->> 'recommendationId')
                  AND plan."Status" = 'Pending';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The superseded state was a defect; it is not restored.
        }
    }
}
