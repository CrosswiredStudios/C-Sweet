namespace CSweet.Infrastructure.Persistence;

// Shared by the migration and real PostgreSQL fixtures. Older workers cannot accept
// schema 4/5 output for these reviewed generations, or restart a frozen predecessor.
internal static class EpisodeReextractionGuards
{
    internal const string RestoreOutputGuard = """
        CREATE OR REPLACE FUNCTION csweet_episode_reconciliation_output_valid(input text,source_hash text,output jsonb) RETURNS boolean
        LANGUAGE sql IMMUTABLE AS $$
          WITH policy AS (SELECT input::jsonb AS s,input::jsonb->'Reconciliation' AS p)
          SELECT CASE WHEN p IS NULL OR p='null'::jsonb OR output IS NULL THEN true ELSE COALESCE(
            output->'SchemaVersion'='5'::jsonb AND output->>'GenericSourceHash'=source_hash AND
            output->'Episode'=s->'Episode' AND output->'Reconciliation'=p AND
            jsonb_typeof(output->'Enrichment')='object' AND jsonb_typeof(output->'Provider')='object' AND
            length(output->>'ExtractorVersion')>0,false) END FROM policy
        $$;
        """;
    internal const string Install = """
        CREATE OR REPLACE FUNCTION csweet_episode_reconciliation_output_valid(input text,source_hash text,output jsonb) RETURNS boolean
        LANGUAGE sql IMMUTABLE AS $$
          WITH policy AS (SELECT input::jsonb AS s,input::jsonb->'Reconciliation' AS p,input::jsonb->'Reextraction' AS v)
          SELECT CASE WHEN output IS NULL THEN true WHEN v IS NOT NULL AND v<>'null'::jsonb THEN COALESCE(
            output->'SchemaVersion'='6'::jsonb AND output->>'GenericSourceHash'=source_hash AND
            output->'Episode'=s->'Episode' AND output->'Reextraction'=v AND
            COALESCE(output->'Reconciliation','null'::jsonb)=COALESCE(p,'null'::jsonb) AND
            jsonb_typeof(output->'Enrichment')='object' AND jsonb_typeof(output->'Provider')='object' AND
            length(output->>'ExtractorVersion')>0,false)
          WHEN p IS NULL OR p='null'::jsonb THEN true ELSE COALESCE(
            output->'SchemaVersion'='5'::jsonb AND output->>'GenericSourceHash'=source_hash AND
            output->'Episode'=s->'Episode' AND output->'Reconciliation'=p AND
            jsonb_typeof(output->'Enrichment')='object' AND jsonb_typeof(output->'Provider')='object' AND
            length(output->>'ExtractorVersion')>0,false) END FROM policy
        $$;
        CREATE FUNCTION csweet_episode_reextraction_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE v jsonb;
        BEGIN
          v=NEW."SourceJson"::jsonb->'Reextraction';
          IF NEW."InputGeneration" NOT BETWEEN 0 AND 8 OR
             (NEW."InputGeneration"=0 AND (NEW."PreviousJobId" IS NOT NULL OR v IS NOT NULL AND v<>'null'::jsonb)) OR
             (NEW."InputGeneration">0 AND NOT COALESCE(jsonb_typeof(v)='object' AND v->'Version'='1'::jsonb AND
               v->'Generation'=to_jsonb(NEW."InputGeneration") AND v->>'PreviousJobId'=NEW."PreviousJobId"::text AND
               (v->>'ReviewReceiptId')::uuid<>'00000000-0000-0000-0000-000000000000',false)) THEN
            RAISE EXCEPTION 'memory_episode_reextraction_input_invalid' USING ERRCODE='23514';
          END IF;
          IF TG_OP='INSERT' THEN
            IF NEW."SupersededAt" IS NOT NULL OR NEW."InputGeneration"=0 AND EXISTS(
              SELECT 1 FROM "MemoryEpisodeReextractionReceipts" WHERE "EpisodeId"=NEW."EpisodeId") THEN
              RAISE EXCEPTION 'memory_episode_reextraction_history_required' USING ERRCODE='23514';
            END IF;
          ELSE
            IF (NEW."InputGeneration",NEW."PreviousJobId") IS DISTINCT FROM (OLD."InputGeneration",OLD."PreviousJobId") OR
               OLD."SupersededAt" IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
              RAISE EXCEPTION 'memory_episode_reextraction_predecessor_immutable' USING ERRCODE='23514';
            END IF;
            IF NEW."SupersededAt" IS DISTINCT FROM OLD."SupersededAt" AND
               (NEW."SupersededAt" IS NULL OR OLD."Status" NOT IN('Failed','Completed') OR OLD."LeaseToken" IS NOT NULL OR
                OLD."LeaseExpiresAt">clock_timestamp() OR
                to_jsonb(NEW)-'SupersededAt' IS DISTINCT FROM to_jsonb(OLD)-'SupersededAt' OR EXISTS(
                  SELECT 1 FROM "MemoryEnrichmentProviderLeases" WHERE "JobId"=OLD."Id" AND "ExpiresAt">clock_timestamp())) THEN
              RAISE EXCEPTION 'memory_episode_reextraction_job_busy' USING ERRCODE='23514';
            END IF;
          END IF;
          IF NEW."InputGeneration">0 AND NEW."Status"='Completed' AND NEW."AcceptedExtractionJson" IS NULL OR
             NOT csweet_episode_reconciliation_output_valid(NEW."SourceJson",NEW."SourceHash",NEW."AcceptedExtractionJson") THEN
            RAISE EXCEPTION 'memory_episode_reextraction_output_unbound' USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER csweet_episode_reextraction_guard BEFORE INSERT OR UPDATE ON "MemoryEpisodeEnrichmentJobs"
          FOR EACH ROW EXECUTE FUNCTION csweet_episode_reextraction_guard();
        CREATE FUNCTION csweet_episode_reextraction_receipt_guard() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'memory_episode_reextraction_receipt_immutable' USING ERRCODE='23514'; END IF;
          IF NEW."Id"='00000000-0000-0000-0000-000000000000' OR NEW."OperationId"='00000000-0000-0000-0000-000000000000' OR
             NEW."ActorOrganizationUserId"='00000000-0000-0000-0000-000000000000' OR
             NEW."ActorApplicationUserId"='00000000-0000-0000-0000-000000000000' OR NEW."InputGeneration" NOT BETWEEN 1 AND 8 OR
             length(NEW."RequestHash")<>64 OR NEW."RequestHash" ~ '[^0-9a-f]' OR
             length(NEW."PreviousJobHash")<>64 OR NEW."PreviousJobHash" ~ '[^0-9a-f]' OR
             length(NEW."SourceHash")<>64 OR NEW."SourceHash" ~ '[^0-9a-f]' OR
             NEW."PreviousAcceptedHash" IS NOT NULL AND (length(NEW."PreviousAcceptedHash")<>64 OR NEW."PreviousAcceptedHash" ~ '[^0-9a-f]') THEN
            RAISE EXCEPTION 'memory_episode_reextraction_receipt_invalid' USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER csweet_episode_reextraction_receipt_guard BEFORE INSERT OR UPDATE OR DELETE ON "MemoryEpisodeReextractionReceipts"
          FOR EACH ROW EXECUTE FUNCTION csweet_episode_reextraction_receipt_guard();
        CREATE FUNCTION csweet_episode_reextraction_transition_valid(receipt uuid) RETURNS boolean LANGUAGE sql AS $$
          SELECT EXISTS(SELECT 1 FROM "MemoryEpisodeReextractionReceipts" r
            JOIN "MemoryEpisodeEnrichmentJobs" old ON old."Id"=r."PreviousJobId"
            JOIN "MemoryEpisodeEnrichmentJobs" next ON next."Id"=r."JobId"
            WHERE r."Id"=receipt AND r."OrganizationId"=old."OrganizationId" AND r."EmployeeId"=old."EmployeeId" AND
              r."EpisodeId"=old."EpisodeId" AND r."InputGeneration"=old."InputGeneration"+1 AND old."Status" IN('Failed','Completed') AND
              old."SupersededAt"=r."CreatedAt" AND old."LeaseToken" IS NULL AND
              r."PreviousAcceptedHash" IS NOT DISTINCT FROM CASE WHEN old."AcceptedExtractionJson" IS NULL THEN NULL ELSE
                lower(encode(sha256(convert_to(old."AcceptedExtractionJson"::text,'UTF8')),'hex')) END AND
              (next."OrganizationId",next."EmployeeId",next."InstallationId",next."ReviewerApplicationUserId",next."EpisodeId") IS NOT DISTINCT FROM
              (old."OrganizationId",old."EmployeeId",old."InstallationId",old."ReviewerApplicationUserId",old."EpisodeId") AND
              next."PreviousJobId"=old."Id" AND next."InputGeneration"=r."InputGeneration" AND next."CreatedAt"=r."CreatedAt" AND
              next."SourceHash"=r."SourceHash" AND (next."SourceJson"::jsonb->'Reextraction'->>'ReviewReceiptId')::uuid=r."Id" AND
              next."SourceJson"::jsonb->'Episode'->>'SourceFingerprint'=old."SourceJson"::jsonb->'Episode'->>'SourceFingerprint' AND
              (next."SourceJson"::jsonb->'Episode'->>'Sensitivity')::integer>=(old."SourceJson"::jsonb->'Episode'->>'Sensitivity')::integer)
        $$;
        CREATE FUNCTION csweet_episode_reextraction_commit() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE receipt uuid;
        BEGIN
          IF TG_TABLE_NAME='MemoryEpisodeReextractionReceipts' THEN
            receipt=NEW."Id";
            IF NOT EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" WHERE "Id"=NEW."JobId" AND "Status"='Pending' AND
              "Attempts"=0 AND "RetryGeneration"=0 AND "LeaseToken" IS NULL AND "LeaseExpiresAt" IS NULL AND
              "AcceptedExtractionJson" IS NULL AND "ExtractionAcceptedAt" IS NULL AND "SupersededAt" IS NULL) OR NOT EXISTS(
              SELECT 1 FROM "CoreOrganizationUsers" WHERE "Id"=NEW."ActorOrganizationUserId" AND
                "OrganizationId"=NEW."OrganizationId" AND "ApplicationUserId"=NEW."ActorApplicationUserId" AND
                "EmployeeType"='Human' AND "IsActive" AND "ArchivedAt" IS NULL) THEN
              RAISE EXCEPTION 'memory_episode_reextraction_queue_required' USING ERRCODE='23514';
            END IF;
          ELSE
            IF NEW."InputGeneration">0 THEN
              receipt=(NEW."SourceJson"::jsonb->'Reextraction'->>'ReviewReceiptId')::uuid;
              IF NOT csweet_episode_reextraction_transition_valid(receipt) THEN
                RAISE EXCEPTION 'memory_episode_reextraction_receipt_required' USING ERRCODE='23514';
              END IF;
            END IF;
            IF NEW."SupersededAt" IS NULL THEN RETURN NEW; END IF;
            SELECT "Id" INTO receipt FROM "MemoryEpisodeReextractionReceipts" WHERE "PreviousJobId"=NEW."Id";
          END IF;
          IF receipt IS NULL OR NOT csweet_episode_reextraction_transition_valid(receipt) THEN
            RAISE EXCEPTION 'memory_episode_reextraction_receipt_required' USING ERRCODE='23514';
          END IF;
          RETURN NEW;
        END $$;
        CREATE CONSTRAINT TRIGGER csweet_episode_reextraction_commit AFTER INSERT OR UPDATE ON "MemoryEpisodeEnrichmentJobs"
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION csweet_episode_reextraction_commit();
        CREATE CONSTRAINT TRIGGER csweet_episode_reextraction_receipt_commit AFTER INSERT ON "MemoryEpisodeReextractionReceipts"
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION csweet_episode_reextraction_commit();
        """;
}
