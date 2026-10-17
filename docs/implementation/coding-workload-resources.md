# Coding workload resources and recovery

Verified: October 16, 2026. Implementation entry points are `WorkloadResourcePolicy`,
`AgentRuntimeManager.TryStartAsync`, `AgentWorkInbox.StageResourceHandoffAsync`,
`GitWorkspaceCapabilityHandler.SyncWorkspaceAsync` and Office's `GuestWorkloadSupervisor`.

## Policy and placement

`AgentRuntimeGlobalSettings.CodingInitialMemoryMb` defaults to 4096 MiB for the
`software-development-polyglot-v1` environment. `CodingMaximumMemoryMb` defaults to
16384 MiB and `CodingMaximumReplacements` to two. These are administrator-controlled
coding policy settings, separate from coordination grants and agent-package builder
memory. Existing coding grants are not rewritten. Package upgrades cannot reset the
coding policy through `AgentDefinitionInstallationSynchronizer`.

`WorkloadResourcePolicy` rounds to 2/4/8/16 GiB tiers. An unknown coding project starts
at 4 GiB. The initial allocation also honors a larger installation allocation within
the coding ceiling. Proactive estimates may select an approved larger tier directly;
confirmed memory failures retry at the next tier. Both use the same two-replacement bound.
Lowering the ceiling parks an
oversized pending operation in `WaitingForPolicy`; it does not create repeated failed
VM starts. The existing signed Office resource specification and scheduler's aggregate
CPU/RAM/disk reservations still govern admission. Insufficient host capacity queues work.

`AgentRuntimeInstance.AllocatedMemoryMb` records the actual requested guest allocation;
`AgentRuntimeRunResponse` also exposes peak workload bytes and a typed resource failure.
The resource snapshot in `AgentWorkLease.Resources` identifies the allocation, ceiling,
replacement generation, recovery state and certified guest-image digest.

## Checkpoint and replacement

`WorkspaceResourceSizing.BeforeCommandAsync` inventories bounded project manifests and
lockfiles before heavy commands. An initial deterministic preflight runs before dependency
installation or model inference, so a known larger profile can yield without repeating either.
Unknown projects request 4 GiB; larger project graphs
can estimate 8 GiB. The confined model can also request a larger tier through
`request_workload_memory`; that tool checkpoints and ends the callback. Estimates are advisory. A dependency fingerprint includes the guest
image digest, so changing dependencies or the certified toolchain invalidates old hints.
An assigned repository's last 64 resource profiles are returned with its authorized
source snapshot. Other repositories and organizations are not exposed.

Before a heavy command, `PlatformGitWorkspaceClient.UploadCheckpointAsync` saves source,
resource inputs and a bounded execution summary. A larger-tier request yields a typed
`AgentWorkResult.ResourceHandoff`. The server rechecks the live lease, installation grant
revision, current ticket assignment, exact workspace revision, checkpoint digest and
checkpoint runtime. It rejects concurrent active callbacks and unconfirmed memory claims.
`RuntimeCheckpointGate` serializes uploads and handoff settlement with the same installation
gate and PostgreSQL advisory lock, preventing an in-flight old upload from changing retained
source after a replacement has been authorized.

The same transaction finishes that inbox attempt, retains the same work identity and
deadline, increments the bounded replacement generation, queues the work, marks the old
runtime Stopping and writes its resource-change notification outbox record. The original
stage attempt is not failed or recreated. Replacement attempts do not consume ordinary
coding failure attempts. Claims from the stopping VM are fenced. A new VM cannot be
created for that installation until provider stop/destroy confirmation completes.

Durable queue reconciliation rediscovers missed notifications. Events are wake hints,
never execution grants. Duplicate handoffs with the same completion hash have one effect.
Personal-work replacement carries the exact server-validated root item identity; the SDK
rereads its revision and resumes that root rather than picking unrelated personal work.

## Memory failure evidence

Office 0.10.0 delegates a private memory/pids cgroup subtree to its guest service.
The new launch protection is enabled by the host-authenticated coding resource envelope;
older coordination starts without those fields retain their previous behavior.
`GuestWorkloadSupervisor` moves broker supervision into one child and launches artifact
code in another, before dropping privileges. The workload tree receives at most 75% of
the lesser of physical and authorized VM memory, with at least 256 MiB reserved for
supervision. Swap is disabled for that subtree; `memory.oom.group` terminates its whole
process tree on a cgroup OOM. The workload user cannot change controller limits.

The supervisor reads `memory.events` and `memory.peak`. A kernel-confirmed `oom_kill`
produces `workload-memory-limit-exceeded` through the authenticated guest exit, with a
peak measurement. Headquarters persists this separately from arbitrary process log text.
Exit 137, a model assertion, a timeout and a missing heartbeat are not memory proof.

`AgentWorkInbox.RecoverResourceFailureAsync` uses the last completed pre-command checkpoint
for a bounded larger-tier retry. It can recover a late typed exit that raced normal VM
teardown. Missing checkpoints, changed authority, simultaneous callbacks, exhausted
replacement budgets or an exhausted ceiling enter `DiagnosisRequired` instead. A generic
lease expiry never increases memory. Successful supervisor measurements update future
profile recommendations with guest overhead and 30% burst headroom. Measurements are
recorded once per runtime. This implementation does not automatically downsize a profile.

## Other reliability changes

- `BoundedWorkspaceShell` independently bounds cancellation callbacks and cleanup. Its
  failure or resource handoff stops model continuation. Clean Windows shells retain
  `PATHEXT` so installed CLI executables are not misclassified as unavailable. Snapshots cannot race an uncertain
  writer. Typed command failures returned through normal completion also fence the VM.
- `WorkspaceValidation` captures executed status, binds receipts to current source and
  reruns missing/stale evidence. Pipelines, command chaining, appended echo and shell status
  masking cannot supply a passing validation. Retained summaries guide resumption but
  never authorize publication without fresh current-source evidence.
- `AgentWorkAttempt.LastOperationDigest` allows repeated interruption of the same operation
  to stop ordinary coding retries for infrastructure diagnosis. Producer diagnostics read
  the same queued/running/replacement/policy/diagnosis state as the scheduler.
- `ProjectHealthCapabilityHandler.Classify` distinguishes stale revision/evidence, revoked
  authority, invalid reports, idempotency conflicts and persistence failures.
  `ProjectIncidentReview` refreshes stale manager evidence once and reassesses it; denials
  and unbounded retry loops remain prohibited.
- Technical Director `BindDependencyPins` carries concrete structured dependency pins into
  engineering briefs. An unresolved Phaser pin becomes an executable engineering task
  whose integrated result gates dependent engineering. A future human kickoff cannot
  masquerade as a resolved package version.
- `BrowserDeliveryRecipe` provides a dependency-free Node server and a multi-stage Docker
  recipe using `COPY --from=build`. Real HTTP tests exercise root, assets, missing paths,
  malformed encoding, traversal, methods and HEAD. This is delivery readiness evidence;
  actual game/browser acceptance remains independent QA.
- SDK snapshot enumeration prunes dependency and metadata directories before traversing
  them. Existing per-VM package caches remain available. Shared cross-VM dependency caches
  and separately sized build workers are not introduced by this change.

## Verification and rollout boundary

Regression coverage lives in `AgentWorkInboxTests.Resources`, `WorkloadResourcePolicyTests`,
both developers' `WorkspaceResourceSizingTests`, `WorkspaceShellTests` and
`BrowserDeliveryRecipeTests`, SDK `ResourceHandoffTests`/`ProjectHealthTests`, Office
`GuestResourceProtectionTests`, and Technical Director `DependencyPinPlanningTests`.
Run `scripts/Test-DeveloperAgentCoreParity.ps1` for the mirrored developer cores.

Verified source/package checks:

| Component | Result |
| --- | --- |
| Headquarters resource/recovery selection | 421 passing tests; three PostgreSQL tests additionally passed against a disposable database |
| SDK 4.6.0 | 445 passing tests; generated standalone authoring template: seven passing tests and self-test |
| Adrian 4.1.0 and Daniel 2.1.0 | 274 passing package-only Release tests each; 53-file core parity check; packages and self-tests verified |
| Technical Director 4.2.0 | 150 passing tests; package and self-test verified |
| Office 0.10.0 | 241 passing tests and independent Release build with published contracts |
| Delivery evidence | Real Git scripted-model delivery journey passed; clean Docker build and HTTP recipe checks passed |

Both migrations have no pending model changes and produce an idempotent SQL script.
Package checks inspected the produced NuGet IDs and versions, with sibling source
references disabled for consumer checks. Test artifacts remain under
`artifacts/resource-sizing/`. The live business database and installed agents were not changed.

The installed delivery path still requires a rebuilt guest and real installed-runtime
acceptance. Office certification suite v16 adds a disposable memory-pressure VM; it must
emit the authenticated memory-limit exit and a nonzero measured peak while its broker
survives. Development image discovery requires v16, so older v15 images cannot silently
stand in for this guarantee. Ordinary noncoding network inventory remains compatible with
v15. Run the complete implementation → publication → independent review → trusted merge →
exact-commit QA journey after installation, including lost responses, stale input,
duplicate/out-of-order events and runtime replacement. Source tests and the existing
scripted `CSweet.DeliveryWorkflow.Tests` journey do not certify that installed path.

Apply both resource migrations through the normal migrator, configure the certified v16
development image, release SDK/agents/Office in dependency order, and verify the host can
reserve at least one 4 GiB coding VM. Do not apply these changes to a running business by
silently replacing its certified guest or bypassing Office authorization.
