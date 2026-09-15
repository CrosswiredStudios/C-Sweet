using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillCommentEditDeleteGrants : Migration
    {
        private const string CommentActions = """
            ('work.item.comment'),
            ('work.item.comments.read'),
            ('work.item.comment.update.v1'),
            ('work.item.comment.delete.v1')
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Members who could already read work items now receive the default comment authority
            // that WorkBoardProvisioning grants to new organizations. Updates and deletions remain
            // author-scoped in the service, so these grants never widen a member's reach beyond the
            // comments they wrote themselves.
            migrationBuilder.Sql($"""
                INSERT INTO "ScopedActionGrants" (
                    "Id", "OrganizationId", "SubjectKind", "SubjectId", "Action",
                    "ScopeKind", "ScopeId", "CanDelegate", "ParentGrantId",
                    "GrantedBySubjectKind", "GrantedBySubjectId", "Revision",
                    "GrantedAt", "ExpiresAt", "RevokedAt")
                SELECT
                    md5('comment-edit-delete-v1:' || existing."SubjectId"::text || ':' ||
                        existing."ScopeKind" || ':' || COALESCE(existing."ScopeId"::text, '') || ':' ||
                        granted."Action")::uuid,
                    existing."OrganizationId",
                    existing."SubjectKind",
                    existing."SubjectId",
                    granted."Action",
                    existing."ScopeKind",
                    existing."ScopeId",
                    FALSE,
                    NULL,
                    existing."GrantedBySubjectKind",
                    existing."GrantedBySubjectId",
                    1,
                    now(),
                    NULL,
                    NULL
                FROM "ScopedActionGrants" existing
                INNER JOIN "CoreOrganizationUsers" member
                    ON member."Id" = existing."SubjectId" AND member."IsActive"
                CROSS JOIN (VALUES
                    {CommentActions}) granted("Action")
                WHERE existing."SubjectKind" = 'OrganizationUser'
                  AND existing."Action" = 'work.item.read'
                  AND existing."RevokedAt" IS NULL
                  AND existing."ScopeKind" IN ('Organization', 'Board')
                  AND (existing."ExpiresAt" IS NULL OR existing."ExpiresAt" > now())
                  AND (existing."ScopeKind" = 'Organization' OR EXISTS (
                      SELECT 1 FROM "WorkBoards" board
                      WHERE board."Id" = existing."ScopeId" AND board."ArchivedAt" IS NULL))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "ScopedActionGrants" found
                      WHERE found."OrganizationId" = existing."OrganizationId"
                        AND found."SubjectKind" = 'OrganizationUser'
                        AND found."SubjectId" = existing."SubjectId"
                        AND found."Action" = granted."Action"
                        AND found."ScopeKind" = existing."ScopeKind"
                        AND found."ScopeId" IS NOT DISTINCT FROM existing."ScopeId"
                        AND found."RevokedAt" IS NULL);
                """);

            // Personal boards authorize with the personal-todo family, so their owners are granted
            // the same comment actions directly on the board. Managers are deliberately excluded:
            // a manager may read a report's personal board but must not post on it.
            migrationBuilder.Sql($"""
                INSERT INTO "ScopedActionGrants" (
                    "Id", "OrganizationId", "SubjectKind", "SubjectId", "Action",
                    "ScopeKind", "ScopeId", "CanDelegate", "ParentGrantId",
                    "GrantedBySubjectKind", "GrantedBySubjectId", "Revision",
                    "GrantedAt", "ExpiresAt", "RevokedAt")
                SELECT
                    md5('comment-edit-delete-v1:' || board."OwnerOrganizationUserId"::text || ':Board:' ||
                        board."Id"::text || ':' || granted."Action")::uuid,
                    board."OrganizationId",
                    'OrganizationUser',
                    board."OwnerOrganizationUserId",
                    granted."Action",
                    'Board',
                    board."Id",
                    FALSE,
                    NULL,
                    'OrganizationUser',
                    board."OwnerOrganizationUserId",
                    1,
                    now(),
                    NULL,
                    NULL
                FROM "WorkBoards" board
                INNER JOIN "CoreOrganizationUsers" owner
                    ON owner."Id" = board."OwnerOrganizationUserId" AND owner."IsActive"
                CROSS JOIN (VALUES
                    {CommentActions}) granted("Action")
                WHERE board."Kind" = 'Personal'
                  AND board."ArchivedAt" IS NULL
                  AND board."OwnerOrganizationUserId" IS NOT NULL
                  AND owner."EmployeeType" = 'Human'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "ScopedActionGrants" found
                      WHERE found."OrganizationId" = board."OrganizationId"
                        AND found."SubjectKind" = 'OrganizationUser'
                        AND found."SubjectId" = board."OwnerOrganizationUserId"
                        AND found."Action" = granted."Action"
                        AND found."ScopeKind" = 'Board'
                        AND found."ScopeId" = board."Id"
                        AND found."RevokedAt" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM "ScopedActionGrants" grant_row
                USING "ScopedActionGrants" existing
                CROSS JOIN (VALUES
                    {CommentActions}) granted("Action")
                WHERE existing."Action" = 'work.item.read'
                  AND existing."SubjectKind" = 'OrganizationUser'
                  AND existing."ScopeKind" IN ('Organization', 'Board')
                  AND grant_row."Id" = md5(
                      'comment-edit-delete-v1:' || existing."SubjectId"::text || ':' ||
                      existing."ScopeKind" || ':' || COALESCE(existing."ScopeId"::text, '') || ':' ||
                      granted."Action")::uuid;
                """);

            migrationBuilder.Sql($"""
                DELETE FROM "ScopedActionGrants" grant_row
                USING "WorkBoards" board
                CROSS JOIN (VALUES
                    {CommentActions}) granted("Action")
                WHERE board."Kind" = 'Personal'
                  AND board."OwnerOrganizationUserId" IS NOT NULL
                  AND grant_row."Id" = md5(
                      'comment-edit-delete-v1:' || board."OwnerOrganizationUserId"::text || ':Board:' ||
                      board."Id"::text || ':' || granted."Action")::uuid;
                """);
        }
    }
}
