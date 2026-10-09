using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations;

/// <summary>Durable generations for canonical case execution bindings.</summary>
public partial class ScopedCaseConsumerAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(AuthorityTriggers);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "MemoryCaseConsumerAuthority" WHERE "Revision">1)
                OR EXISTS (SELECT 1 FROM "AgentMemoryReadReceipts" WHERE "EvidenceJson" LIKE '%"scopedAuthorityHash"%') THEN
                RAISE EXCEPTION 'Case consumer authority has been used; downgrading would discard execution revocation history.';
            END IF;
        END $$;
        DROP TRIGGER csweet_memory_case_consumer_authority ON "WorkSprintExecutions";
        DROP TRIGGER csweet_memory_case_consumer_authority ON "WorkItemExecutions";
        DROP TRIGGER csweet_memory_case_consumer_authority ON "WorkStageExecutions";
        DROP TRIGGER csweet_memory_case_consumer_authority ON "WorkExecutionAttempts";
        DROP TRIGGER csweet_memory_case_consumer_authority ON "WorkDeliveryPlans";
        DROP TRIGGER csweet_memory_case_consumer_authority ON "WorkDeliveryExecutions";
        DROP TRIGGER csweet_memory_case_consumer_truncate ON "WorkSprintExecutions";
        DROP TRIGGER csweet_memory_case_consumer_truncate ON "WorkItemExecutions";
        DROP TRIGGER csweet_memory_case_consumer_truncate ON "WorkStageExecutions";
        DROP TRIGGER csweet_memory_case_consumer_truncate ON "WorkExecutionAttempts";
        DROP TRIGGER csweet_memory_case_consumer_truncate ON "WorkDeliveryPlans";
        DROP TRIGGER csweet_memory_case_consumer_truncate ON "WorkDeliveryExecutions";
        DROP TABLE "MemoryCaseConsumerAuthority";
        DROP FUNCTION csweet_memory_case_consumer_authority();
        DROP FUNCTION csweet_memory_case_consumer_guard();
        DROP FUNCTION csweet_memory_case_consumer_truncate();
        """);

    public const string AuthorityTriggers = """
        LOCK TABLE "WorkSprintExecutions","WorkItemExecutions","WorkStageExecutions","WorkExecutionAttempts","WorkDeliveryPlans","WorkDeliveryExecutions"
            IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE "MemoryCaseConsumerAuthority" (
            "Kind" text NOT NULL CHECK ("Kind" IN ('Sprint','Item','Stage','Attempt','Plan','Delivery')),
            "Id" uuid NOT NULL,
            "Revision" bigint NOT NULL DEFAULT 1 CHECK ("Revision">0),
            PRIMARY KEY ("Kind","Id")
        );
        INSERT INTO "MemoryCaseConsumerAuthority" ("Kind","Id")
            SELECT 'Sprint',"Id" FROM "WorkSprintExecutions"
            UNION ALL SELECT 'Item',"Id" FROM "WorkItemExecutions"
            UNION ALL SELECT 'Stage',"Id" FROM "WorkStageExecutions"
            UNION ALL SELECT 'Attempt',"Id" FROM "WorkExecutionAttempts"
            UNION ALL SELECT 'Plan',"Id" FROM "WorkDeliveryPlans"
            UNION ALL SELECT 'Delivery',"Id" FROM "WorkDeliveryExecutions";

        CREATE FUNCTION csweet_memory_case_consumer_guard() RETURNS trigger AS $$
        BEGIN
            IF pg_trigger_depth()>1 THEN
                IF TG_OP='INSERT' AND NEW."Revision"=1 THEN RETURN NEW; END IF;
                IF TG_OP='UPDATE' AND NEW."Revision"=OLD."Revision"+1
                    AND ROW(NEW."Kind",NEW."Id")=ROW(OLD."Kind",OLD."Id") THEN RETURN NEW; END IF;
            END IF;
            RAISE EXCEPTION 'Case consumer authority history is immutable.' USING ERRCODE='23514';
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_case_consumer_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryCaseConsumerAuthority"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_guard();

        CREATE FUNCTION csweet_memory_case_consumer_authority() RETURNS trigger AS $$
        DECLARE changed boolean;
        BEGIN
            IF TG_OP='INSERT' THEN
                INSERT INTO "MemoryCaseConsumerAuthority" ("Kind","Id") VALUES (TG_ARGV[0],NEW."Id")
                    ON CONFLICT ("Kind","Id") DO UPDATE SET "Revision"="MemoryCaseConsumerAuthority"."Revision"+1;
                IF TG_ARGV[0]='Attempt' THEN
                    UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Kind"='Stage' AND "Id"=NEW."StageExecutionId";
                ELSIF TG_ARGV[0]='Stage' THEN
                    UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE
                        ("Kind"='Item' AND "Id"=NEW."ItemExecutionId") OR ("Kind"='Delivery' AND "Id"=NEW."DeliveryExecutionId");
                END IF;
                RETURN NEW;
            ELSIF TG_OP='DELETE' THEN
                UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Kind"=TG_ARGV[0] AND "Id"=OLD."Id";
                IF TG_ARGV[0]='Attempt' THEN
                    UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Kind"='Stage' AND "Id"=OLD."StageExecutionId";
                ELSIF TG_ARGV[0]='Stage' THEN
                    UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE
                        ("Kind"='Item' AND "Id"=OLD."ItemExecutionId") OR ("Kind"='Delivery' AND "Id"=OLD."DeliveryExecutionId");
                END IF;
                RETURN OLD;
            END IF;
            IF NEW."Id" IS DISTINCT FROM OLD."Id" THEN
                RAISE EXCEPTION 'Case execution identities cannot be changed.' USING ERRCODE='23514';
            END IF;
            IF TG_ARGV[0]='Sprint' THEN
                changed := ROW(NEW."OrganizationId",NEW."BoardId",NEW."PolicyRevisionId",NEW."Status")
                    IS DISTINCT FROM ROW(OLD."OrganizationId",OLD."BoardId",OLD."PolicyRevisionId",OLD."Status");
            ELSIF TG_ARGV[0]='Item' THEN
                changed := ROW(NEW."SprintExecutionId",NEW."WorkItemId",NEW."CurrentStageKey",NEW."Traversal",NEW."Status")
                    IS DISTINCT FROM ROW(OLD."SprintExecutionId",OLD."WorkItemId",OLD."CurrentStageKey",OLD."Traversal",OLD."Status");
            ELSIF TG_ARGV[0]='Stage' THEN
                changed := ROW(NEW."ItemExecutionId",NEW."DeliveryExecutionId",NEW."StageKey",NEW."StageType",NEW."Traversal",
                    NEW."Status",NEW."PrincipalKind",NEW."OrganizationUserId",NEW."AgentInstallationId")
                    IS DISTINCT FROM ROW(OLD."ItemExecutionId",OLD."DeliveryExecutionId",OLD."StageKey",OLD."StageType",OLD."Traversal",
                    OLD."Status",OLD."PrincipalKind",OLD."OrganizationUserId",OLD."AgentInstallationId");
            ELSIF TG_ARGV[0]='Plan' THEN
                changed := ROW(NEW."OrganizationId",NEW."WorkstreamId",NEW."ManagerOrganizationUserId",NEW."Status",NEW."ScopeRevision",
                    NEW."ScopesJson",NEW."EpicItemIdsJson",NEW."BranchesJson")
                    IS DISTINCT FROM ROW(OLD."OrganizationId",OLD."WorkstreamId",OLD."ManagerOrganizationUserId",OLD."Status",OLD."ScopeRevision",
                    OLD."ScopesJson",OLD."EpicItemIdsJson",OLD."BranchesJson");
            ELSIF TG_ARGV[0]='Delivery' THEN
                changed := ROW(NEW."PlanId",NEW."Scope",NEW."WorkItemId",NEW."BoardId",NEW."ScopeRevision",NEW."Status",NEW."CurrentStageKey",NEW."CandidateJson")
                    IS DISTINCT FROM ROW(OLD."PlanId",OLD."Scope",OLD."WorkItemId",OLD."BoardId",OLD."ScopeRevision",OLD."Status",OLD."CurrentStageKey",OLD."CandidateJson");
            ELSE
                changed := ROW(NEW."StageExecutionId",NEW."AgentWorkItemId",NEW."Attempt",NEW."Status",NEW."IdempotencyKey")
                    IS DISTINCT FROM ROW(OLD."StageExecutionId",OLD."AgentWorkItemId",OLD."Attempt",OLD."Status",OLD."IdempotencyKey");
            END IF;
            IF changed THEN
                UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Kind"=TG_ARGV[0] AND "Id"=OLD."Id";
                IF TG_ARGV[0]='Attempt' THEN
                    UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE "Kind"='Stage'
                        AND "Id" IN (OLD."StageExecutionId",NEW."StageExecutionId");
                ELSIF TG_ARGV[0]='Stage' THEN
                    UPDATE "MemoryCaseConsumerAuthority" SET "Revision"="Revision"+1 WHERE
                        ("Kind"='Item' AND "Id" IN (OLD."ItemExecutionId",NEW."ItemExecutionId")) OR
                        ("Kind"='Delivery' AND "Id" IN (OLD."DeliveryExecutionId",NEW."DeliveryExecutionId"));
                END IF;
            END IF;
            RETURN NEW;
        END; $$ LANGUAGE plpgsql;
        CREATE FUNCTION csweet_memory_case_consumer_truncate() RETURNS trigger AS $$
        BEGIN
            RAISE EXCEPTION 'Truncation would discard case consumer authority history.' USING ERRCODE='23514';
        END; $$ LANGUAGE plpgsql;
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "MemoryCaseConsumerAuthority"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        CREATE TRIGGER csweet_memory_case_consumer_authority BEFORE INSERT OR UPDATE OR DELETE ON "WorkSprintExecutions"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_authority('Sprint');
        CREATE TRIGGER csweet_memory_case_consumer_authority BEFORE INSERT OR UPDATE OR DELETE ON "WorkItemExecutions"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_authority('Item');
        CREATE TRIGGER csweet_memory_case_consumer_authority BEFORE INSERT OR UPDATE OR DELETE ON "WorkStageExecutions"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_authority('Stage');
        CREATE TRIGGER csweet_memory_case_consumer_authority BEFORE INSERT OR UPDATE OR DELETE ON "WorkExecutionAttempts"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_authority('Attempt');
        CREATE TRIGGER csweet_memory_case_consumer_authority BEFORE INSERT OR UPDATE OR DELETE ON "WorkDeliveryPlans"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_authority('Plan');
        CREATE TRIGGER csweet_memory_case_consumer_authority BEFORE INSERT OR UPDATE OR DELETE ON "WorkDeliveryExecutions"
            FOR EACH ROW EXECUTE FUNCTION csweet_memory_case_consumer_authority('Delivery');
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "WorkSprintExecutions"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "WorkItemExecutions"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "WorkStageExecutions"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "WorkExecutionAttempts"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "WorkDeliveryPlans"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        CREATE TRIGGER csweet_memory_case_consumer_truncate BEFORE TRUNCATE ON "WorkDeliveryExecutions"
            FOR EACH STATEMENT EXECUTE FUNCTION csweet_memory_case_consumer_truncate();
        """;
}
