# Compute page and Linux preparation

## Page and ownership

`src/CSweet.UI/Pages/Compute.razor` separates **Compute types** from **Instances** using the
existing Aurora business-workspace theme. Linux is the only available type. Owners can request
setup, retry a confirmed failure, and create instances for an approved agent's current project.
Other active human members can inspect status. Existing release and local-test-link permissions
remain owner-controlled.

`Components/ComputePreparation.razor` renders six milestones: requirements, image download,
Ubuntu installation, image finalization, compute service connection, and readiness verification.
Already-ready setup starts collapsed on page entry. When observed preparation transitions from
`Pending` or `Running` to `Ready`, completed details remain visible for two seconds, then collapse.
The user can reopen **View preparation**. A manual toggle cancels the scheduled collapse;
ordinary refreshes do not collapse the details again. Non-ready state/attempt changes expand the panel,
and organization changes replace the component and cancel its timer.

When Linux preparation fails, **Needs Your Attention** shows the business owner one card with the
cause, the impact on work such as browser QA, and an **Open Compute** link for that business.
`OverviewAttentionItems.FailedComputePreparation` reads the current setup on page load, on
reconnect and after `ComputeChanged`. Repeated QA waits share the same card. Starting another
preparation clears the failed state and removes the card.

`ComputeManagementEndpoints` provides owner-authenticated setup, creation options and instance
requests. `ComputeDefaultsService.ListCreationOptionsAsync` lists at most 100 current agent/project
pairs. `CreateAsync` rechecks project authority through `ReadProjectAsync` and provisions through
the existing `IComputeBroker`; the selector is not authorization. Linux/x64 and the current default
size (2 CPUs, 2 GiB memory, 20 GiB disk) are server-selected. Lifetime cannot exceed configured
limits. Existing grants can impose stricter limits or deny the request. No network grant is added
by manual creation; previously reviewed hiring grants retain their normal behavior.

The creation request ID remains stable across retries. Unconfirmed requests retain their original
terms in the form. The broker owns idempotency, quotas, placement and atomic operation/wake
outbox publication. Owner-request audit records join the broker's save; replay requests that have
no persisted effect need not add another audit record. Setup requests are serialized by the existing
business lock and save their audit outbox record with the new setup. Refreshing the dashboard or
viewing creation options does not create instances or grants.

## Preparation evidence

`ComputeLocalSetup.PreparationAttemptId` identifies the current installer attempt independently
of the one-use enrollment secret. `ComputeLocalSetupWorker` sets it when claiming setup and
includes it in the application-owned handoff. Failure updates are guarded by the observed attempt
and state, so an older process cannot fail a newer request. Apply migration
`AddComputePreparationAttempt` through the normal application migration workflow before running
the updated API/gateway. Existing rows use a nullable identity and retain legacy observation support.

`ComputeLocalSetupWorker` writes `CSweet.Compute.Runtime.ComputeLocalSetupHandoff` with `ComputeProtocol.Json`;
`LocalComputeInstaller.ReadHandoffAsync` reads that same type. The optional `AttemptId` accepts
older handoffs without disabling strict rejection of unknown fields. An explicit empty attempt
identity is invalid. Installer-only changes do not change the guest image fingerprint, so a service
repair can reuse the verified image.

`scripts/Write-ComputeSetupProgress.ps1` atomically replaces a version-2 snapshot containing the
attempt ID, phase key, attempt start, phase start, last milestone time and the last 24 phase events.
Repeated reports from the same phase do not advance milestone timestamps. New attempts reset
history. `Ensure-ComputeLinuxImage.ps1` forwards phase keys from the shared image builder and
reports image verification/cache reuse; `Install-ComputeLocalProvider.ps1` reports service checks,
connection and readiness verification. `Write-ComputeInstallerReceipt.ps1` includes the attempt ID
in process and terminal receipts so final evidence remains usable after the secret is revoked.

`ComputePreparationProgress.Read` accepts only the current attempt, recognized phase keys and
valid ordered timestamps. UI messages are mapped from an allowlist, never copied from installer
transcripts, error output, secrets or host paths. A failed attempt retains its last milestone and
activity; a pending retry does not inherit the previous attempt's activity. Malformed, truncated,
unknown-phase and different-attempt snapshots are not reported as current progress.

`ComputeSetupObservation` checks process identity (PID plus start time) and attempt-matched
terminal receipts. `ComputePreparationProgress.Observe` keeps that evidence separate from
milestone age. A running process is labeled **Process running**, not a claimed guest heartbeat or
proof of advancement. Missing/unreadable process evidence yields **Status unknown**, not a
failure. A quiet but running installer has explanatory text rather than a stale-progress warning.
Retry requires terminal failure evidence or persisted failure and is blocked while the matched
installer is still running. Declined elevation has a distinct **Needs permission** action.

`Write-ComputeInstallerReceipt.ps1` records a fixed failure code for configuration parsing,
component checks, provider publishing, enrollment, service startup or readiness reporting.
`ComputeSetupObservation` accepts only known codes from the matched attempt; arbitrary exception
text, paths and secrets are never returned. `ComputeLocalSetupWorker` persists that code on exit,
and `ComputePreparationProgress.Observe` prefers specific receipt evidence over an older generic
failure. `ComputePreparation` explains the failed step and recovery action, including image reuse.
Older generic service-stage failures also identify that the image finished before service setup failed.

Download percentages and estimates come from measured bytes and `ComputeDownloadProgress`.
The 10–30 minute image-build duration remains an expected range, never a remaining-time estimate
or an overall percentage. The page retains its existing ten-second status reads; no new agent
polling loops are introduced. The local snapshot is durable state, not an execution grant.
Existing compute-available and broker outboxes continue to wake agents; current authorization and
state reads remain authoritative.

## Installation-owned base images

On Windows, **Settings → Storage → Compute drive** controls bulk storage on subsequent local
setups. `HostStorageSettings` saves the host administrator's volume ID in the Headquarters
profile and `ComputeLocalSetupWorker` includes the effective choice in its installer handoff.
The default uses the registered Office volume. `Initialize-ComputeStorage` revalidates the
mounted disk and protects `<drive>:\CSweetCompute` independently of Office removal. Under that
directory, `prepared` holds verified images, `build` holds downloads and retained Packer
exports, and `base` holds digest-addressed installed images. Fresh enrollments create a private
`workloads-<business>-<node>` directory. Existing providers retain their workload paths on
repair. Service binaries, journals and retirement receipts use the protected ProgramData layout.
Changing the preference schedules no migration or deletion. The legacy layout below also
applies when neither an explicit selection nor an Office registration exists.

`ComputeSharedImageStore` in `CSweet.Compute.HyperV` publishes verified VHDX images to
`%ProgramData%\CSweet\ComputeImages\<sha256>.vhdx`. This protected directory is a sibling of
the legacy `Compute` and business-scoped `ComputeBusinesses` directories. It is never part of
business retirement. Image identity is content-based and independent of business, source filename
and guest operating system; the current automatic preparation workflow still supplies Linux/x64.

`Install-ComputeLocalProvider.ps1` passes the prepared image to
`LocalComputeInstaller.ConfigureAsync` without making a business-local image copy. The installer
imports it into the shared store and writes that path into the provider's own certified catalog.
`Ensure-ComputeLinuxImage.ps1` continues to serialize preparation and reuse its verified build
cache, so creating a business does not require another download/build of unchanged image inputs.

Publication uses a private staging file, SHA-256 verification, disk flush and atomic no-overwrite
rename. Concurrent installers converge on the same verified file. Existing files are verified before
reuse; corrupt images fail setup rather than being overwritten under other providers. Store paths
require trusted Windows ownership/ACLs and reject redirected paths. Providers retain their own
certifications, enrollment, grants, quotas, journals and workload directories. `HyperVComputeDriver`
still copies the base into a private writable VM disk; no writable disks are shared.

Normal `repair-local` updates also import legacy images after verifying the exact enrolled source
path and digest. `PreserveEnrolledTemplate` retains the original template metadata and features.
The existing runtime updater backs up catalog/settings together with its runtime and restores them
on validation failure. Prior image files remain available for rollback and existing installs continue
to work until their next normal repair/update. No live migration is performed by source changes.

Published shared images and old business-local copies are retained; automatic garbage collection
is not implemented. Future reclamation must account for every provider catalog, rollback reference
and in-flight image reader. Business deletion must never delete the installation-owned store.

## Verification

- `ComputeProgressRenderingTests`: progress/ETA, quiet versus unknown, owner controls, and delayed collapse.
- `ComputePreparationProgressTests`: attempt isolation, safe text, retained terminal history, receipt identity, malformed evidence and health/retry semantics.
- `ComputeLocalSetupHandoffTests`: actual installer-reader roundtrip, legacy compatibility, and strict rejection of unknown or invalid handoff fields.
- `ComputeSharedImageStoreTests`: cross-business reuse, concurrent publication, version coexistence,
  corruption/cancellation, protected-file rejection, repair identity preservation and retirement boundaries.
- `ComputeCatalogInstallationTests`: independent certified providers concurrently open one shared base image.
- `ComputeManagementTests`: owner/business scope, idempotent setup/creation, project and lifetime checks, revoked-grant preservation.
- `ComputePreparationMigrationTests`: model/snapshot agreement and additive nullable SQL.
- `tests/ComputeSetupProgress.Tests.ps1`, `tests/ComputeInstallerReceipt.Tests.ps1`, and `tests/AutomaticComputeImage.Tests.ps1`: atomic progress snapshots, bounded/reset history, process receipts and image-cache behavior.

Tests use fixtures. Live Hyper-V image creation, system elevation and end-to-end guest readiness
still require host acceptance; a passing render or unit test does not establish those outcomes.

### Host preparation acceptance — October 16, 2026

The handoff repair passed 502 compute tests and 16 PowerShell setup tests. After rebuilding
and restarting the local API and frontend through Aspire, **Retry preparation** for Sweet Games
completed automatically from 5:01:40 PM to 5:03:01 PM Pacific. The attempt activity recorded
`checking → cached → check-service → connect-service → verify-readiness`; its terminal receipt
was `Completed` and its own acceptance TRX recorded 502 passed, zero failed/error tests.

The image receipt retained fingerprint `0ed0e5c9b500c8ce30a68d4469f92f253ca99e4b5348d588564e0b79e5a28c07`
and SHA-256 `eb9ad8ce64855eb2eeb12d7bfd631f337b6455cfa84cae43ee66c5a5b69b62cd`.
The business compute service was Running with Automatic startup. The Compute page showed
**Ready**, all six milestones complete and **Reusing the verified Linux image** in activity.
An existing Prism Break instance request resumed automatically and reached Bootstrapping.
Screenshot evidence is saved locally at `artifacts/tests/compute-handoff/compute-ready.png`.
