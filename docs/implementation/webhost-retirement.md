# WebHost retirement runbook

The WebHost/private-preview proof of concept is retired. Core no longer registers its API routes, MCP tools, approval executor, runtime dispatch or private-preview UI. Software Developer 1.0.0, Software QA 1.0.0 and Game Engineer 3.0.0 remove their preview callbacks and package dependencies. Shared execution, isolation and the development-only static artifact viewer remain.

## Deployment order

1. Before replacing a running control plane, disable new PoC requests and drain existing previews using the previous version. Stop every assigned workload and obtain provider confirmation. Keep node identity, assignment and protected VM records until this is complete.
2. Verify there are no rows in WebPreviewJobs with TeardownConfirmedAt unset. Expiration or a failed command alone is not teardown confirmation. If the old control plane cannot complete cleanup, reconcile the exact recorded VM identities with the installed privileged runtime and record verified outcomes using the existing recovery process. Do not clear this field merely to bypass migration.
3. Stop and decommission the retired Node/RuntimeHost installations after all their VMs are gone. Revoke their enrolled identities and remove obsolete service configuration, host certificates/keys, preview DNS/TLS and published plugin installations through the applicable operator procedures. Preserve required audit evidence before removing protected state. This repository change does not execute any of these live operations.
4. Deploy the new Core and agent versions and apply `20260912012616_RetireWebHostProofOfConcept`. The migration refuses to drop tables while unconfirmed jobs remain. It cancels only pending `web-preview.grant` proposals, marks only pending `com.csweet.web-preview.changed.v1` outbox events failed, and drops the nine PoC tables.
5. Upgrade the affected agent installations through normal reviewed manifests. Retired preview capabilities are not mapped to compute permissions. Remove obsolete optional WebPreviews installations and binding configuration; do not upgrade them into infrastructure authority.

## Data and rollback

Back up the database using normal deployment procedures before applying this destructive migration. Historical approvals and decision artifacts, copied QA tickets, ordinary delivery builds, PreviewSessions and execution records are preserved. PoC browser sessions, raw evidence, grants, commands, jobs and host enrollments are discarded.

The generated Down migration can recreate empty PoC schema, but cannot recover discarded rows, cancelled proposals, failed notifications or installed runtime state. Rollback requiring historical PoC operation needs the database backup and matching old runtime/control-plane binaries. Do not run old and new control planes concurrently during this cutover.

No live database migration, service removal or VM deletion was performed as part of source editing. Generic compute is a separate implementation tracked in [the migration map](compute-migration.md); retirement alone does not provide a working compute provider.
