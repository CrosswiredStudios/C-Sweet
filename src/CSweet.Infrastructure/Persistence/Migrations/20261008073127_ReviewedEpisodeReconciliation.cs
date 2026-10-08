using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewedEpisodeReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InstallGuards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" WHERE
                    "SourceJson"::jsonb->'Reconciliation' IS NOT NULL AND "SourceJson"::jsonb->'Reconciliation'<>'null'::jsonb) THEN
                    RAISE EXCEPTION 'Retain reviewed reconciliation guards while reconciliation jobs exist';
                  END IF;
                END $$;
                DROP TRIGGER csweet_episode_reconciliation_guard ON "MemoryEpisodeEnrichmentJobs";
                DROP FUNCTION csweet_episode_reconciliation_guard();
                DROP FUNCTION csweet_episode_reconciliation_output_valid(text,text,jsonb);
                DROP FUNCTION csweet_episode_reconciliation_input_valid(text);
                """);
        }

        public const string InstallGuards = """
            CREATE OR REPLACE FUNCTION csweet_episode_reconciliation_input_valid(input text) RETURNS boolean
            LANGUAGE sql IMMUTABLE AS $$
              WITH policy AS (SELECT input::jsonb->'Reconciliation' AS p)
              SELECT CASE WHEN p IS NULL OR p='null'::jsonb THEN true ELSE COALESCE(
                jsonb_typeof(p)='object' AND p->'Version'='1'::jsonb AND p->>'Policy'='preserve-existing-v1' AND
                length(p->>'InventoryHash')=64 AND p->>'InventoryHash' !~ '[^0-9a-f]' AND jsonb_typeof(p->'RecordCount')='number' AND
                p->'RecordCount'>='1'::jsonb AND p->'RecordCount'<='256'::jsonb AND
                CASE WHEN jsonb_typeof(p->'Claims')='array' THEN jsonb_array_length(p->'Claims')<=256 ELSE false END AND
                CASE WHEN jsonb_typeof(p->'Edges')='array' THEN jsonb_array_length(p->'Edges')<=256 ELSE false END AND
                CASE WHEN jsonb_typeof(p->'Procedures')='array' THEN jsonb_array_length(p->'Procedures')<=256 ELSE false END AND
                CASE WHEN jsonb_typeof(p->'SourceEpisodeIds')='array' THEN jsonb_array_length(p->'SourceEpisodeIds') BETWEEN 1 AND 128 AND
                  NOT EXISTS(SELECT 1 FROM jsonb_array_elements_text(p->'SourceEpisodeIds') s WHERE
                    s IS NULL OR s !~ '^[0-9a-f]+-[0-9a-f]+-[0-9a-f]+-[0-9a-f]+-[0-9a-f]+$' OR length(s)<>36 OR
                    length(split_part(s,'-',1))<>8 OR length(split_part(s,'-',2))<>4 OR length(split_part(s,'-',3))<>4 OR
                    length(split_part(s,'-',4))<>4 OR length(split_part(s,'-',5))<>12 OR s='00000000-0000-0000-0000-000000000000')
                  ELSE false END,false) END FROM policy
            $$;
            CREATE OR REPLACE FUNCTION csweet_episode_reconciliation_output_valid(input text,source_hash text,output jsonb) RETURNS boolean
            LANGUAGE sql IMMUTABLE AS $$
              WITH policy AS (SELECT input::jsonb AS s,input::jsonb->'Reconciliation' AS p)
              SELECT CASE WHEN p IS NULL OR p='null'::jsonb OR output IS NULL THEN true ELSE COALESCE(
                output->'SchemaVersion'='5'::jsonb AND output->>'GenericSourceHash'=source_hash AND
                output->'Episode'=s->'Episode' AND output->'Reconciliation'=p AND
                jsonb_typeof(output->'Enrichment')='object' AND jsonb_typeof(output->'Provider')='object' AND
                length(output->>'ExtractorVersion')>0,false) END FROM policy
            $$;
            DO $$ BEGIN
              IF EXISTS(SELECT 1 FROM "MemoryEpisodeEnrichmentJobs" WHERE
                NOT csweet_episode_reconciliation_input_valid("SourceJson") OR
                NOT csweet_episode_reconciliation_output_valid("SourceJson","SourceHash","AcceptedExtractionJson") OR
                ("SourceJson"::jsonb->'Reconciliation' IS NOT NULL AND "SourceJson"::jsonb->'Reconciliation'<>'null'::jsonb AND
                  "Status"='Completed' AND "AcceptedExtractionJson" IS NULL)) THEN
                RAISE EXCEPTION 'Reviewed reconciliation jobs require verified versioned output before upgrade';
              END IF;
            END $$;
            CREATE OR REPLACE FUNCTION csweet_episode_reconciliation_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NOT csweet_episode_reconciliation_input_valid(NEW."SourceJson") OR
                 NOT csweet_episode_reconciliation_output_valid(NEW."SourceJson",NEW."SourceHash",NEW."AcceptedExtractionJson") OR
                 (NEW."SourceJson"::jsonb->'Reconciliation' IS NOT NULL AND NEW."SourceJson"::jsonb->'Reconciliation'<>'null'::jsonb AND
                   NEW."Status"='Completed' AND NEW."AcceptedExtractionJson" IS NULL) THEN
                RAISE EXCEPTION 'Reviewed reconciliation policy and extraction output must remain bound' USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER csweet_episode_reconciliation_guard BEFORE INSERT OR UPDATE ON "MemoryEpisodeEnrichmentJobs"
              FOR EACH ROW EXECUTE FUNCTION csweet_episode_reconciliation_guard();
            """;
    }
}
