using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

/// <summary>Persistent authority generations, including deleted/recreated identities.</summary>
public partial class MemoryAccessAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(AuthorityTriggers);

    private static readonly (string Table, string Kind, string Fields)[] Authorities =
    [
        ("CoreOrganizationUsers", "person", "OrganizationId,ApplicationUserId,AgentInstallationId,IsActive,ArchivedAt,EmployeeType,RoleId,ReportsToOrganizationUserId,PermissionLevel"),
        ("AgentInstallations", "installation", "BusinessId,IsEnabled,Scope,RevisionStatus,PackageVersionId,AgentDefinitionId,InstallationKey,ExecutionPoolId"),
        ("OrganizationTeams", "team", "OrganizationId,LeadOrganizationUserId,ArchivedAt"),
        ("TeamMemberships", "member", "OrganizationId,TeamId,OrganizationUserId,TeamRoleId,ExclusiveAgentEmployeeId,JoinedAt,EndedAt"),
        ("CoreRoles", "role", "OrganizationId,AuthorityLevel,ResponsibilitiesJson"),
        ("CoreConversations", "conversation", "OrganizationId,Kind,AgentOrganizationUserId,InitiatedByOrganizationUserId,TeamId,WorkstreamId,IsPrivate,ArchivedAt,MergedIntoConversationId")
    ];

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "MemoryAccessAuthority" WHERE "Revision">1)
                    OR EXISTS (SELECT 1 FROM "AgentMemoryReadReceipts") THEN
                    RAISE EXCEPTION 'Memory access authority has been used; downgrading would discard revocation history.';
                END IF;
            END $$;
            """);
        foreach (var authority in Authorities)
            migrationBuilder.Sql($"DROP TRIGGER csweet_memory_access_authority ON \"{authority.Table}\"; DROP TRIGGER csweet_memory_access_truncate ON \"{authority.Table}\";");
        migrationBuilder.Sql("""
            DROP TRIGGER csweet_memory_access_membership ON "TeamMemberships";
            DROP TRIGGER csweet_memory_access_participant ON "ConversationParticipants";
            DROP TRIGGER csweet_memory_access_grant ON "AgentInstallationGrants";
            DROP TABLE "MemoryAccessAuthority";
            DROP FUNCTION csweet_memory_access_authority();
            DROP FUNCTION csweet_memory_access_related();
            DROP FUNCTION csweet_memory_access_guard();
            DROP FUNCTION csweet_memory_access_truncate();
            """);
    }

    public static string AuthorityTriggers => """
        LOCK TABLE "CoreOrganizationUsers","AgentInstallations","OrganizationTeams","TeamMemberships","CoreRoles",
            "CoreConversations","ConversationParticipants","AgentInstallationGrants" IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE "MemoryAccessAuthority" (
            "Kind" text NOT NULL CHECK ("Kind" IN ('person','installation','team','member','role','conversation')),
            "Id" uuid NOT NULL, "Revision" bigint NOT NULL DEFAULT 1 CHECK ("Revision">0),
            PRIMARY KEY ("Kind","Id")
        );
        """ + string.Join("\n", Authorities.Select(x =>
            $"INSERT INTO \"MemoryAccessAuthority\" (\"Kind\",\"Id\") SELECT '{x.Kind}',\"Id\" FROM \"{x.Table}\";")) + "\n" + """
        CREATE FUNCTION csweet_memory_access_guard() RETURNS trigger AS $$
        BEGIN
            IF pg_trigger_depth()>1 THEN
                IF TG_OP='INSERT' AND NEW."Revision"=1 THEN RETURN NEW; END IF;
                IF TG_OP='UPDATE' AND NEW."Revision"=OLD."Revision"+1
                    AND ROW(NEW."Kind",NEW."Id")=ROW(OLD."Kind",OLD."Id") THEN RETURN NEW; END IF;
            END IF;
            RAISE EXCEPTION 'Memory access authority history is immutable.' USING ERRCODE='23514';
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_access_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryAccessAuthority"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_access_guard();
        CREATE FUNCTION csweet_memory_access_truncate() RETURNS trigger AS $$
        BEGIN RAISE EXCEPTION 'Memory access authority history cannot be truncated.' USING ERRCODE='23514'; END;
        $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_access_truncate BEFORE TRUNCATE ON "MemoryAccessAuthority"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_access_truncate();

        CREATE FUNCTION csweet_memory_access_authority() RETURNS trigger AS $$
        DECLARE field text;
        BEGIN
            IF TG_OP='UPDATE' THEN
                IF NEW."Id" IS DISTINCT FROM OLD."Id" THEN
                    RAISE EXCEPTION 'Memory authority identity cannot be reassigned.' USING ERRCODE='23514';
                END IF;
                FOREACH field IN ARRAY string_to_array(TG_ARGV[1],',') LOOP
                    IF to_jsonb(NEW)->field IS DISTINCT FROM to_jsonb(OLD)->field THEN EXIT; END IF;
                END LOOP;
                IF to_jsonb(NEW)->field IS NOT DISTINCT FROM to_jsonb(OLD)->field THEN RETURN NULL; END IF;
            END IF;
            IF TG_OP='DELETE' THEN
                UPDATE "MemoryAccessAuthority" SET "Revision"="Revision"+1 WHERE "Kind"=TG_ARGV[0] AND "Id"=OLD."Id";
            ELSE
                INSERT INTO "MemoryAccessAuthority" ("Kind","Id") VALUES (TG_ARGV[0],NEW."Id")
                    ON CONFLICT ("Kind","Id") DO UPDATE SET "Revision"="MemoryAccessAuthority"."Revision"+1;
            END IF;
            RETURN NULL;
        END; $$ LANGUAGE plpgsql;

        CREATE FUNCTION csweet_memory_access_related() RETURNS trigger AS $$
        DECLARE field text; before_id uuid; after_id uuid;
        BEGIN
            IF TG_OP='UPDATE' THEN
                FOREACH field IN ARRAY string_to_array(TG_ARGV[2],',') LOOP
                    IF to_jsonb(NEW)->field IS DISTINCT FROM to_jsonb(OLD)->field THEN EXIT; END IF;
                END LOOP;
                IF to_jsonb(NEW)->field IS NOT DISTINCT FROM to_jsonb(OLD)->field THEN RETURN NULL; END IF;
            END IF;
            IF TG_OP IN ('UPDATE','DELETE') THEN before_id := (to_jsonb(OLD)->>TG_ARGV[1])::uuid; END IF;
            IF TG_OP IN ('UPDATE','INSERT') THEN after_id := (to_jsonb(NEW)->>TG_ARGV[1])::uuid; END IF;
            UPDATE "MemoryAccessAuthority" SET "Revision"="Revision"+1
                WHERE "Kind"=TG_ARGV[0] AND ("Id"=before_id OR "Id"=after_id);
            RETURN NULL;
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_access_membership AFTER INSERT OR UPDATE OR DELETE ON "TeamMemberships"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_access_related('person','OrganizationUserId',
                'OrganizationId,TeamId,OrganizationUserId,TeamRoleId,ExclusiveAgentEmployeeId,JoinedAt,EndedAt');
        CREATE TRIGGER csweet_memory_access_participant AFTER INSERT OR UPDATE OR DELETE ON "ConversationParticipants"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_access_related('conversation','ConversationId',
                'ConversationId,OrganizationUserId,Role,JoinedAt,LeftAt');
        CREATE TRIGGER csweet_memory_access_grant AFTER INSERT OR UPDATE OR DELETE ON "AgentInstallationGrants"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_access_related('installation','AgentInstallationId',
                'AgentInstallationId,GrantRevision,RequiredCapabilitiesJson,ProvidedCapabilitiesJson,NetworkAccessJson');
        """ + "\n" + string.Join("\n", Authorities.Select(x => $"""
        CREATE TRIGGER csweet_memory_access_authority AFTER INSERT OR UPDATE OR DELETE ON "{x.Table}"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_access_authority('{x.Kind}','{x.Fields}');
        CREATE TRIGGER csweet_memory_access_truncate BEFORE TRUNCATE ON "{x.Table}"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_access_truncate();
        """));
}
