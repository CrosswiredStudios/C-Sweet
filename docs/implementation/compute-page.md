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

Download percentages and estimates come from measured bytes and `ComputeDownloadProgress`.
The 10–30 minute image-build duration remains an expected range, never a remaining-time estimate
or an overall percentage. The page retains its existing ten-second status reads; no new agent
polling loops are introduced. The local snapshot is durable state, not an execution grant.
Existing compute-available and broker outboxes continue to wake agents; current authorization and
state reads remain authoritative.

## Verification

- `ComputeProgressRenderingTests`: progress/ETA, quiet versus unknown, owner controls, and delayed collapse.
- `ComputePreparationProgressTests`: attempt isolation, safe text, retained terminal history, receipt identity, malformed evidence and health/retry semantics.
- `ComputeManagementTests`: owner/business scope, idempotent setup/creation, project and lifetime checks, revoked-grant preservation.
- `ComputePreparationMigrationTests`: model/snapshot agreement and additive nullable SQL.
- `tests/ComputeSetupProgress.Tests.ps1`, `tests/ComputeInstallerReceipt.Tests.ps1`, and `tests/AutomaticComputeImage.Tests.ps1`: atomic progress snapshots, bounded/reset history, process receipts and image-cache behavior.

Tests use fixtures. Live Hyper-V image creation, system elevation and end-to-end guest readiness
still require host acceptance; a passing render or unit test does not establish those outcomes.
