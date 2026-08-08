using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CSweet.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentDefinitionRuntimeLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentDefinitionId",
                table: "AgentInstallations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AppliedConfigurationRevision",
                table: "AgentInstallations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfigurationSyncLastAttemptAt",
                table: "AgentInstallations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfigurationSyncLastError",
                table: "AgentInstallations",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfigurationSyncStatus",
                table: "AgentInstallations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "DesiredConfigurationRevision",
                table: "AgentInstallations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "AgentInstallationConfigurations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "AgentDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PackageVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsAvailableForHire = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultActivationMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultTickFrequencySeconds = table.Column<int>(type: "integer", nullable: false),
                    DefaultOverlapPolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultMaxRuntimeSeconds = table.Column<int>(type: "integer", nullable: false),
                    DefaultMemoryMb = table.Column<int>(type: "integer", nullable: false),
                    DefaultCpuPercent = table.Column<int>(type: "integer", nullable: false),
                    DefaultProvidedCapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    DefaultRequiredCapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    DefaultEventSubscriptionsJson = table.Column<string>(type: "text", nullable: false),
                    DefaultNetworkAccessJson = table.Column<string>(type: "text", nullable: false),
                    DefaultCapabilityBindingsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDefinitions_AgentPackageSources_PackageSourceId",
                        column: x => x.PackageSourceId,
                        principalTable: "AgentPackageSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentDefinitions_AgentPackageVersions_PackageVersionId",
                        column: x => x.PackageVersionId,
                        principalTable: "AgentPackageVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDefinitionConfigurations_AgentDefinitions_AgentDefinit~",
                        column: x => x.AgentDefinitionId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstallations_AgentDefinitionId",
                table: "AgentInstallations",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionConfigurations_AgentDefinitionId",
                table: "AgentDefinitionConfigurations",
                column: "AgentDefinitionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_PackageSourceId_AgentId",
                table: "AgentDefinitions",
                columns: new[] { "PackageSourceId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_PackageVersionId",
                table: "AgentDefinitions",
                column: "PackageVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentInstallations_AgentDefinitions_AgentDefinitionId",
                table: "AgentInstallations",
                column: "AgentDefinitionId",
                principalTable: "AgentDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill globally installed definitions without creating any runtime installation.
            migrationBuilder.Sql(
                """
                WITH packages AS (
                    SELECT DISTINCT ON (p."PackageSourceId", p."AgentId")
                        p."Id", p."PackageSourceId", p."AgentId", p."DefaultActivationMode",
                        p."Status", p."ImportedAt"
                    FROM "AgentPackageVersions" p
                    WHERE p."PluginKind" = 'Agent'
                    ORDER BY p."PackageSourceId", p."AgentId",
                        CASE WHEN p."Status" = 'Built' THEN 0 ELSE 1 END,
                        p."ImportedAt" DESC
                )
                INSERT INTO "AgentDefinitions" (
                    "Id", "PackageSourceId", "AgentId", "PackageVersionId", "Status",
                    "IsAvailableForHire", "DefaultActivationMode", "DefaultTickFrequencySeconds",
                    "DefaultOverlapPolicy", "DefaultMaxRuntimeSeconds", "DefaultMemoryMb",
                    "DefaultCpuPercent", "DefaultProvidedCapabilitiesJson",
                    "DefaultRequiredCapabilitiesJson", "DefaultEventSubscriptionsJson",
                    "DefaultNetworkAccessJson", "DefaultCapabilityBindingsJson", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), p."PackageSourceId", p."AgentId", p."Id",
                    CASE WHEN p."Status" = 'Built' THEN 'NeedsConfiguration' ELSE 'Building' END,
                    FALSE,
                    COALESCE(s."ActivationMode", NULLIF(p."DefaultActivationMode", ''), 'Manual'),
                    COALESCE(s."TickFrequencySeconds", 300),
                    COALESCE(s."OverlapPolicy", 'Skip'),
                    COALESCE(g."MaxRuntimeSeconds", s."MaxRuntimeSeconds", 86400),
                    COALESCE(g."MemoryMb", 1024), COALESCE(g."CpuPercent", 100),
                    COALESCE(g."ProvidedCapabilitiesJson", '[]'),
                    COALESCE(g."RequiredCapabilitiesJson", '[]'),
                    COALESCE(g."EventSubscriptionsJson", '[]'),
                    COALESCE(g."NetworkAccessJson", '[]'), '{}', NOW(), NOW()
                FROM packages p
                LEFT JOIN LATERAL (
                    SELECT ai."Id"
                    FROM "AgentInstallations" ai
                    JOIN "AgentPackageVersions" aip ON aip."Id" = ai."PackageVersionId"
                    WHERE aip."PackageSourceId" = p."PackageSourceId" AND aip."AgentId" = p."AgentId"
                      AND NOT EXISTS (SELECT 1 FROM "CoreOrganizationUsers" ou
                                      WHERE ou."AgentInstallationId" = ai."Id" AND ou."IsActive")
                    ORDER BY CASE WHEN ai."BusinessId" = 'default' THEN 0 ELSE 1 END, ai."UpdatedAt" DESC
                    LIMIT 1
                ) template ON TRUE
                LEFT JOIN "AgentSchedules" s ON s."AgentInstallationId" = template."Id"
                LEFT JOIN "AgentInstallationGrants" g ON g."AgentInstallationId" = template."Id";

                WITH manifest_defaults AS (
                    SELECT d."Id" AS definition_id,
                        COALESCE(jsonb_object_agg(field->>'key', field->'defaultValue')
                            FILTER (WHERE field ? 'defaultValue' AND COALESCE((field->>'secret')::boolean, FALSE) = FALSE),
                            '{}'::jsonb) AS settings
                    FROM "AgentDefinitions" d
                    JOIN "AgentPackageVersions" p ON p."Id" = d."PackageVersionId"
                    LEFT JOIN LATERAL jsonb_array_elements(
                        COALESCE(p."ManifestJson"::jsonb->'configuration', '[]'::jsonb)) field ON TRUE
                    GROUP BY d."Id"
                ), template_config AS (
                    SELECT d."Id" AS definition_id, c."SchemaVersion", c."SettingsJson"
                    FROM "AgentDefinitions" d
                    LEFT JOIN LATERAL (
                        SELECT ai."Id"
                        FROM "AgentInstallations" ai
                        JOIN "AgentPackageVersions" aip ON aip."Id" = ai."PackageVersionId"
                        WHERE aip."PackageSourceId" = d."PackageSourceId" AND aip."AgentId" = d."AgentId"
                          AND ai."BusinessId" = 'default'
                          AND NOT EXISTS (SELECT 1 FROM "CoreOrganizationUsers" ou
                                          WHERE ou."AgentInstallationId" = ai."Id" AND ou."IsActive")
                        ORDER BY ai."UpdatedAt" DESC LIMIT 1
                    ) template ON TRUE
                    LEFT JOIN "AgentInstallationConfigurations" c ON c."AgentInstallationId" = template."Id"
                )
                INSERT INTO "AgentDefinitionConfigurations" (
                    "Id", "AgentDefinitionId", "SchemaVersion", "SettingsJson", "Revision", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), d."Id", COALESCE(NULLIF(t."SchemaVersion", ''), '1'),
                    (m.settings || COALESCE(template_settings.settings, '{}'::jsonb))::text, 1, NOW(), NOW()
                FROM "AgentDefinitions" d
                JOIN manifest_defaults m ON m.definition_id = d."Id"
                JOIN "AgentPackageVersions" p ON p."Id" = d."PackageVersionId"
                LEFT JOIN template_config t ON t.definition_id = d."Id"
                LEFT JOIN LATERAL (
                    SELECT COALESCE(jsonb_object_agg(entry.key, entry.value), '{}'::jsonb) AS settings
                    FROM jsonb_each(COALESCE(NULLIF(t."SettingsJson", '')::jsonb, '{}'::jsonb)) entry
                    WHERE EXISTS (
                        SELECT 1
                        FROM jsonb_array_elements(COALESCE(p."ManifestJson"::jsonb->'configuration', '[]'::jsonb)) field
                        WHERE field->>'key' = entry.key
                          AND COALESCE((field->>'secret')::boolean, FALSE) = FALSE)
                ) template_settings ON TRUE;

                WITH definition_validity AS (
                    SELECT d."Id" AS definition_id,
                           p."Status" AS package_status,
                           p."PackageDigest" AS package_digest,
                           p."ArtifactSignature" AS artifact_signature,
                           validity.valid
                    FROM "AgentDefinitions" d
                    JOIN "AgentPackageVersions" p ON p."Id" = d."PackageVersionId"
                    CROSS JOIN LATERAL (
                    SELECT NOT EXISTS (
                        SELECT 1
                        FROM jsonb_array_elements(COALESCE(p."ManifestJson"::jsonb->'configuration', '[]'::jsonb)) field
                        WHERE COALESCE((field->>'required')::boolean, FALSE)
                          AND COALESCE((field->>'secret')::boolean, FALSE) = FALSE
                          AND (
                              NOT ((SELECT c."SettingsJson"::jsonb FROM "AgentDefinitionConfigurations" c
                                    WHERE c."AgentDefinitionId" = d."Id") ? (field->>'key'))
                              OR COALESCE((SELECT c."SettingsJson"::jsonb->>(field->>'key')
                                           FROM "AgentDefinitionConfigurations" c
                                           WHERE c."AgentDefinitionId" = d."Id"), '') = '')
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM jsonb_array_elements(COALESCE(p."ManifestJson"::jsonb->'configuration', '[]'::jsonb)) field
                        CROSS JOIN LATERAL (
                            SELECT c."SettingsJson"::jsonb AS settings
                            FROM "AgentDefinitionConfigurations" c
                            WHERE c."AgentDefinitionId" = d."Id"
                        ) config
                        CROSS JOIN LATERAL (
                            SELECT config.settings->(field->>'key') AS value,
                                   LOWER(COALESCE(field->>'type', 'string')) AS field_type
                        ) configured
                        WHERE COALESCE((field->>'secret')::boolean, FALSE) = FALSE
                          AND config.settings ? (field->>'key')
                          AND (
                            (configured.field_type IN ('boolean', 'bool')
                                AND jsonb_typeof(configured.value) <> 'boolean')
                            OR (configured.field_type IN ('number', 'integer') AND (
                                jsonb_typeof(configured.value) <> 'number'
                                OR CASE WHEN jsonb_typeof(configured.value) = 'number' AND field ? 'minimum'
                                    THEN (configured.value #>> '{}')::numeric < (field->>'minimum')::numeric ELSE FALSE END
                                OR CASE WHEN jsonb_typeof(configured.value) = 'number' AND field ? 'maximum'
                                    THEN (configured.value #>> '{}')::numeric > (field->>'maximum')::numeric ELSE FALSE END))
                            OR (configured.field_type IN ('string', 'text', 'textarea', 'model', 'llmmodel')
                                AND jsonb_typeof(configured.value) <> 'string')
                            OR (configured.field_type = 'select' AND (
                                jsonb_typeof(configured.value) <> 'string'
                                OR (jsonb_array_length(COALESCE(field->'options', '[]'::jsonb)) > 0
                                    AND NOT EXISTS (
                                        SELECT 1 FROM jsonb_array_elements(field->'options') option
                                        WHERE option->>'value' = (configured.value #>> '{}')))))
                            OR (configured.field_type IN ('provider', 'llmprovider') AND (
                                jsonb_typeof(configured.value) <> 'string'
                                OR NOT EXISTS (
                                    SELECT 1 FROM "LlmProviderProfiles" profile
                                    WHERE profile."Id" = CASE
                                        WHEN (configured.value #>> '{}') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
                                        THEN (configured.value #>> '{}')::uuid ELSE NULL END
                                      AND profile."IsEnabled")))
                            OR (configured.field_type IN ('model', 'llmmodel')
                                AND jsonb_typeof(configured.value) = 'string'
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM jsonb_array_elements(COALESCE(p."ManifestJson"::jsonb->'configuration', '[]'::jsonb)) provider_field
                                    JOIN "LlmProviderProfiles" profile ON profile."Id" = CASE
                                        WHEN config.settings->>(provider_field->>'key') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$'
                                        THEN (config.settings->>(provider_field->>'key'))::uuid ELSE NULL END
                                    WHERE LOWER(COALESCE(provider_field->>'type', '')) IN ('provider', 'llmprovider')
                                      AND profile."IsEnabled"
                                      AND profile."DefaultChatModel" = (configured.value #>> '{}')))
                            OR (COALESCE(field->>'dependsOnFieldKey', '') <> ''
                                AND COALESCE(config.settings->>(field->>'dependsOnFieldKey'), '') = '')
                          )
                    ) AS valid
                    ) validity
                )
                UPDATE "AgentDefinitions" d
                SET "IsAvailableForHire" = validity.valid AND validity.package_status = 'Built'
                                               AND COALESCE(validity.package_digest, '') <> ''
                                               AND COALESCE(validity.artifact_signature, '') <> '',
                    "Status" = CASE
                        WHEN validity.package_status <> 'Built' OR COALESCE(validity.package_digest, '') = ''
                             OR COALESCE(validity.artifact_signature, '') = '' THEN 'Building'
                        WHEN validity.valid THEN 'Available'
                        ELSE 'NeedsConfiguration' END
                FROM definition_validity validity
                WHERE validity.definition_id = d."Id";

                UPDATE "AgentInstallations" SET "ConfigurationSyncStatus" = 'Current'
                WHERE "ConfigurationSyncStatus" = '';

                UPDATE "AgentInstallations" ai
                SET "AgentDefinitionId" = d."Id",
                    "DesiredConfigurationRevision" = 1,
                    "AppliedConfigurationRevision" = 0,
                    "ConfigurationSyncStatus" = 'PendingNextStart'
                FROM "AgentPackageVersions" p, "AgentDefinitions" d
                WHERE p."Id" = ai."PackageVersionId"
                  AND p."PluginKind" = 'Agent'
                  AND d."PackageSourceId" = p."PackageSourceId" AND d."AgentId" = p."AgentId";

                WITH sparse_configs AS (
                    SELECT c."Id" AS configuration_id,
                           COALESCE(jsonb_object_agg(entry.key, entry.value)
                               FILTER (WHERE dc."SettingsJson"::jsonb->entry.key IS DISTINCT FROM entry.value),
                               '{}'::jsonb) AS settings
                    FROM "AgentInstallationConfigurations" c
                    JOIN "AgentInstallations" ai ON ai."Id" = c."AgentInstallationId"
                    JOIN "AgentDefinitionConfigurations" dc ON dc."AgentDefinitionId" = ai."AgentDefinitionId"
                    CROSS JOIN LATERAL jsonb_each(c."SettingsJson"::jsonb) entry
                    WHERE EXISTS (SELECT 1 FROM "CoreOrganizationUsers" ou
                                  WHERE ou."AgentInstallationId" = ai."Id" AND ou."IsActive")
                    GROUP BY c."Id"
                )
                UPDATE "AgentInstallationConfigurations" c
                SET "SettingsJson" = sparse.settings::text, "Revision" = 1
                FROM sparse_configs sparse
                WHERE sparse.configuration_id = c."Id";

                UPDATE "AgentInstallationConfigurations" SET "Revision" = 1 WHERE "Revision" = 0;

                UPDATE "AgentSchedules" s SET "IsEnabled" = FALSE, "NextTickAt" = NULL
                FROM "AgentInstallations" ai
                JOIN "AgentPackageVersions" p ON p."Id" = ai."PackageVersionId"
                WHERE s."AgentInstallationId" = ai."Id" AND p."PluginKind" = 'Agent'
                  AND NOT EXISTS (SELECT 1 FROM "CoreOrganizationUsers" ou
                                  WHERE ou."AgentInstallationId" = ai."Id" AND ou."IsActive");

                UPDATE "AgentInstallations" ai SET "IsEnabled" = FALSE, "RevisionStatus" = 'Retired'
                FROM "AgentPackageVersions" p
                WHERE p."Id" = ai."PackageVersionId" AND p."PluginKind" = 'Agent'
                  AND NOT EXISTS (SELECT 1 FROM "CoreOrganizationUsers" ou
                                  WHERE ou."AgentInstallationId" = ai."Id" AND ou."IsActive");

                UPDATE "AgentRuntimeInstances" r
                SET "Status" = 'Cancelled', "Reason" = 'Retired unassigned runtime during agent-definition migration.',
                    "CompletedAt" = NOW()
                FROM "AgentInstallations" ai
                JOIN "AgentPackageVersions" p ON p."Id" = ai."PackageVersionId"
                WHERE r."AgentInstallationId" = ai."Id" AND p."PluginKind" = 'Agent'
                  AND r."Status" IN ('Queued','Starting','WaitingForMcpSession','Running','CompletionReported','Stopping')
                  AND NOT EXISTS (SELECT 1 FROM "CoreOrganizationUsers" ou
                                  WHERE ou."AgentInstallationId" = ai."Id" AND ou."IsActive");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentInstallations_AgentDefinitions_AgentDefinitionId",
                table: "AgentInstallations");

            migrationBuilder.DropTable(
                name: "AgentDefinitionConfigurations");

            migrationBuilder.DropTable(
                name: "AgentDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_AgentInstallations_AgentDefinitionId",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "AgentDefinitionId",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "AppliedConfigurationRevision",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "ConfigurationSyncLastAttemptAt",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "ConfigurationSyncLastError",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "ConfigurationSyncStatus",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "DesiredConfigurationRevision",
                table: "AgentInstallations");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "AgentInstallationConfigurations");
        }
    }
}
