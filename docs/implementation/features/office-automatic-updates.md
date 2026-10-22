# Automatic Office updates

`OfficeUpdateDialog` presents one review and **Start update** action. `ExecutionFleet` shows Office cards, a resumable background progress entry, and separate connection recovery. Ordinary updates have no second installation or resume action. Windows may still require its administrator prompt.

## State and ownership

`LocalOfficeSetupSession.AutomaticUpdate`, `UpdatePhase`, `TargetVersion`, and `ResumeAfterUpdate` persist update intent. `ExecutionNode.WorkPauseReason` distinguishes an explicit user pause from a maintenance pause; the draining state alone cannot establish user intent. `OfficeUpdateJob` retains interrupted assignment identities and recovery intent. `ExecutionFleetService.Updates` advances these records through preparing, stopping, installing, reconnecting, and completion; `ExecutionFleetReconciliationWorker` owns bounded restart/missed-event recovery. Browsers observe current state and never grant the next execution step.

`UpdateChanged` inserts the audit and `OfficeUpdateChanged` notification outbox records in the same save as the transition. Notifications are wake hints; the dialog reloads authorized session state on notification/reconnect and uses bounded status refresh as recovery. Duplicate or reordered notifications do not replay installation.

## Safety gates

- `BeginOfficeUpdatePreparationAsync` exchanges a five-minute one-use handoff for a two-hour machine-bound preparation receipt. The original handoff is consumed before lengthy download/certification. Neither receipt grants workload authority.
- `Start-CSweetDevelopmentOfficeSetup.ps1` and Office's installed `Start-CSweetOfficeAutomaticUpdate.ps1` prepare the exact selected payload, verify its executable version, and complete host certification before reporting readiness. Local source building requires an explicit reviewed source choice; published update requests never silently fall back to source.
- `StopOfficeUpdateJobsAsync` takes the placement pool lock before snapshotting assignments and pausing dispatch. It fences assignment and runtime broker authority atomically. `ExecutionAssignmentAttempt.StoppedAt` proves physical teardown, including previous assignment epochs; cancellation, lease expiry, or a zero active status count do not prove shutdown.
- `CanUpgradeOfficeAsync`, `MaintenanceReadyAsync`, preflight, and redemption retain their independent checks. A new installer handoff is issued only after package preparation and confirmed teardown. No installer authorization is issued during cancellation.
- `ReconcileOfficeUpdateAsync` requires a heartbeat newer than the installer callback, the exact selected version, a current certificate, and the existing provider/image/certification and capacity qualification policy before restoring the previous work setting.
- Updates originally started from Ready or a known maintenance pause resume automatically. An explicitly user-paused Office remains paused. A failed-update retry carries the original setting and outstanding recovery intent forward rather than mistaking the maintenance pause for a user pause. Unknown legacy pauses remain conservative. The `OfficeWorkPauseReason` migration backfills them as user pauses except where an unresolved durable automatic update proves explicit resume intent.

## Interrupted work

`RecoverOfficeUpdateJobsAsync` waits for teardown and terminal old runtimes, then queues eligible replacements. `RecoverPlannedWorkAsync` preserves protected payloads/checkpoints, settles the old attempt as `office-update-interrupted`, extends the deadline by the interruption, and exempts maintenance interruptions from the failure-attempt budget. Old runtime broker sessions and assignment epochs remain fenced. Builders are queued through `IAgentBuildService`; toolchain builds retain their identity and use fresh claims/epochs without spending a maintenance interruption as a failed attempt. Jobs that cannot restart retain a recovery error for operator attention.

Cancellation is allowed only before installation authorization. During download it leaves running jobs alone. After stopping starts, it waits for outstanding teardown and healthy capacity, keeps the installed version, and restores the prior work setting. Installation failures stay paused and expose diagnostics and connection recovery; reinstall remains separately reviewed. A successful, explicitly reviewed connection repair can restore the original work policy and interrupted jobs after a failed update, while the update remains marked failed until its selected version is installed. Retry downloads preserve the original interruption interval and recovery intent even if preparation fails again.

## Deployment boundary

Office.Contracts 0.11.0 adds the preparation DTOs and is pinned by both consumers. Office 0.12.0 packages the remote helper. Existing remote Offices below 0.12.0 require the current installer once; the UI and API reject an automatic flow that their installed configurator cannot execute. The configured AppHost development machine uses the server launcher, including its existing Office installation.

`AutomaticOfficeUpdates` and `OfficeUpdateRecoveryAccounting` migrations persist the state, job recovery, and maintenance retry accounting. Source builds/tests do not replace installed Office payloads. Publish/install the independent Office release through its normal release process; apply Headquarters migrations through the migrator.

`OfficeWorkPauseReason` adds pause ownership. The manual pause/resume controls set or clear it and queue an audit record atomically. Maintenance never turns a user pause into its own pause. The global alert uses the server's `work-paused` qualification check to show healthy paused capacity as an informational notice. Office cards expose a primary **Resume work** control. Completed-update text reflects the current work state so a later resume does not leave a stale “remains paused” claim.

## Code and validation map

- UI: `CSweet.UI/Setup/OfficeUpdateDialog`, `CSweet.UI/Pages/ExecutionFleet`, `ExecutionCapacityAlertState`.
- Coordinator: `CSweet.Infrastructure/Setup/ExecutionFleetService.Updates`, `ExecutionFleetReconciliationWorker`, `AgentRuntimeManager.RecoverInterruptedStopAsync`.
- Wire: `CSweet.Office.Contracts/ControlPlane/OfficeUpdatePreparation`; `OfficeLocalSetupEndpoints` and `SetupEndpoints` map both gateway and development bootstrap routes.
- Regression coverage: `AutomaticOfficeUpdateTests`, `SettingsWorkspaceRenderingTests.FocusedUpdateDialogShowsTheAuthoritativeStepWithoutExtraInstallOrResumeClicks`, `ExecutionCapacityAlertStateTests`, `OfficeLocalSetupEndpointTests`, `OfficeRelease.Tests.ps1`, and Office.Contracts `OfficeUpdatePreparationTests`.

## Verification (2026-10-21)

- Headquarters Release: 239 targeted update, fleet, UI rendering, capacity, durable-work, and runtime-replacement tests passed with `UseLocalOfficeContracts=false`. Work already stopping before maintenance is not queued for recovery.
- Office Release solution: 245 tests passed with `UseLocalOfficeContracts=false`.
- Office.Contracts Release: 19 tests passed with `UseLocalIsolation=false`; packed and inspected `CSweet.Office.Contracts.0.11.0.nupkg`, including its version and assembly. Both consumers pin 0.11.0.
- Windows PowerShell 5.1: 76 Office release checks, seven development-payload cases, and four actual automatic-preparation branch scenarios passed. The preparation harness also evaluates the actual preflight/redemption request construction and checks the installed helper matches Headquarters.
- Initial verification reported no pending model changes. The original implementation tests did not apply migrations or replace a running installation.
- Six focused-dialog states are covered by HTML rendering tests. The browser transport was disconnected during initial verification; PostgreSQL placement-race integration was not run.

## Live update and pause correction (2026-10-21)

The user-started update `73c80039-8491-4b7f-8345-c954a848923b` completed the real Hyper-V certification, installed Office 0.12.0, and passed authenticated reconnection and health qualification at 21:28 PDT. Node, RuntimeHost, and Maintenance all run from the new development payload. There were no interrupted jobs to recover in this attempt. Its inherited pause setting left work disabled, exposing that draining state had been confused with deliberate pause intent.

Work was resumed through the authenticated Offices control. Browser inspection confirmed **Ready**, a running job, and the absence of the red capacity banner. The historical update text remained stale in that running build; the current-state message correction and pause-ownership/UI changes above require the next Headquarters build/restart and `OfficeWorkPauseReason` migration. They require no additional Office update.

The corrective Headquarters Release verification passed 260 targeted tests with `UseLocalOfficeContracts=false`, including maintenance versus deliberate pauses, completed-update text after resume, healthy-paused classification, and the prominent resume control. The actual migration backfill SQL passed six PostgreSQL cases against transaction-scoped temporary tables; no live application rows were changed by that verification. Entity Framework reports no pending model changes.
