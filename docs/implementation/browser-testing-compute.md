# Reusable browser testing compute

## Workload and runtime

`CSweet.Compute.Contracts.ComputeWorkload.BrowserTest` is a reusable alternative to `Command` or
`PublishPort`, exposed by `ComputeMcpTools` through the grant-governed `compute.execute.v1` operation.
`ComputeBrowserTest` requires an exact source commit, build SHA-256, request ID and one to four viewport
widths. It selects a fixed guest runner and derives every guest path from the validated build digest.
Callers stage `build.zip` and an empty `evidence` directory under
`/var/lib/csweet-compute/work/browser-<buildSha256>/`. There is no arbitrary browser URL or host path.

`ComputeBroker.SubmitWorkloadAsync` checks the environment's completed provisioning template snapshot
for `browser-testing-v1`. A newer catalog entry cannot give an existing VM features it never had.
`ComputeDispatchVerifier` independently checks the provider's certified template. Signed workload
digests, scoped grants, operation idempotency, generation fences and result validation use the existing
compute path. The browser is inside a hardware-isolated compute VM. Docker within that VM packages the
tooling; it does not replace the VM isolation boundary.

`build/compute-linux/browser/Dockerfile` pins the official Playwright 1.64.0 Noble image by digest and
locks Playwright/core npm integrity hashes. `provision-guest.sh` builds the guest-local image and runs a
real Chromium/WebGL probe before completing image preparation. The image-build fingerprint includes
the runner and lockfiles. `LocalComputeInstaller.ConfigureAsync` advertises the prepared feature;
`Install-ComputeLocalProvider.ps1` refreshes older installations through normal compute preparation.
Existing VMs keep their original image and provisioning snapshot.

`ComputeBrowserTestExecutor` executes the named browser container and confirms its removal, including
cancellation/timeout paths. Unknown outcomes are never blindly rerun. The container has no network,
read-only input/root, bounded memory/CPU/PIDs and temporary storage. Only the evidence directory is
writable. `extract.py` verifies archive bytes before bounded extraction, rejecting traversal, links,
duplicate/case-colliding paths and missing `index.html`. `runner.cjs` serves only that extracted static
build on container loopback. External browser requests fail rather than acquiring network authority.

## Observations and QA binding

The runner records Chromium version, exact commit/build/request identities and per-viewport startup,
console/page errors, failed HTTP/assets, visible canvas and initialized WebGL evidence. Screenshots,
Playwright traces and `result.json` form a digest-identified evidence ZIP. This is startup smoke coverage,
not gameplay, input interaction, FPS, GPU performance, real-device support or backend-app certification.
Default QA guidance requests widths 375 and 1280; viewport height is 720. WebGL can be disabled explicitly
for other static applications. Assets must be self-contained.

QA 5.1.0 `GameQaBrowserTesting` exposes `test_web_build` in the existing independent QA harness. QA builds
the authorized source locally first; the tool rechecks original source hashes, deterministically archives
the actual output and binds the immutable archive to its authorized commit and source/planning identity.
This proves which independently produced QA build was tested; it does not claim a separately published
release artifact was tested. It never changes tracked source or retargets QA to a mutable URL.

`GameQaBrowserTesting.BrowserState` checkpoints requests before submission, including literal terms,
stable keys, generations and cursors. Lost submission replies replay exactly those terms; known operation
IDs are read only. Build parts and evidence parts survive workspace/runtime replacement in operating
state. After digest verification, screenshots/traces are retained in `GameQaJournal` as paginated
`browser-artifact` observations, independent of the compute VM's lifetime. Current limits are 16 MiB
compressed/64 MiB expanded build, 10,000 files, four widths, 30 seconds per operation and 4 MiB evidence.

`WorkOrchestrator.AcceptComputeWaitAsync` accepts `awaiting-compute` only from the assigned task QA
attempt, bound to an authorized workspace commit, reviewer-owned project environment, current operation
generation and bounded recovery time. It retains the current quality stage without recording a pass,
advancing delivery or spending a failure retry. `ComputeResultReconciler` updates the waiting stage's
retry cursor in the result transaction; existing atomic compute events remain wake hints. Already-arrived
results are detected on wait admission, and the five-minute current-state recovery covers missed wakes.
Agents do not poll. Dispatch and source authorization run again on resume.
The pending review reserves its assignee slot while releasing the agent runtime. Another ticket cannot
advance the shared project browser VM's generation before the first review resumes. Completed compute
wakes do not count against the stage's failure retry budget.

Final QA evidence includes the browser operation and exact build/evidence hashes. Failed browser checks
cannot produce a passing report. `WorkOrchestrator.ValidateBrowserEvidenceAsync` checks those references
against the retained successful compute receipt and exact current tested commit before recording quality
acceptance. Browser observations alone never satisfy unrelated criteria.

## Verification and rollout

`ComputeMcpTools.WorkloadSchema` accepts command arguments up to the existing
`ComputeGuestCommand.MaximumTextBytes` limit. The broker and guest still enforce the combined
32 KiB UTF-8 budget for executable, working directory and all arguments. A browser archive part
contains 18,000 bytes, or 24,000 base64 characters, plus the fixed upload script. The previous
24,000-character schema limit rejected that script before dispatch. `ComputeSdkContractTests`
reproduces this full-sized SDK upload through MCP validation and verifies the combined guest
budget remains enforced. Resume the retained request after updating AgentHost; do not alter its
build bytes, command, idempotency key or operating-state checkpoint.

QA 5.1.1 `GameQaBrowserTesting.AdvanceAsync` refreshes the environment after submitting a retained
request without an operation ID. Acceptance advances the generation, so validating against the
pre-submission snapshot falsely reported a different operation identity. The refreshed read still
enforces the original expected-generation and environment fences. `BrowserRecoveryTests` covers
lost requests before acceptance as well as replies lost after acceptance. Publish/install the agent
normally; do not patch installed payloads or retained browser state to apply this source fix.

Run `node --test tests/browserComputeRunner.test.cjs`,
`python -B -m unittest discover -s tests -p test_browser_compute.py`, the compute/QA wait regressions,
and QA's full suite/self-test. `scripts/Test-BrowserCompute.py` exercises real Chromium at both widths
against authored WebGL, JavaScript error, missing asset, missing WebGL and external-request fixtures.
It retains screenshots/traces under `artifacts/browser-compute`. These local container fixtures verify
the runner; they do not certify an installed Hyper-V VM or a live agent/provider handoff.

Deploy the matching platform, drain obsolete compute instances and prepare the new image through the
normal authorized compute setup. Then publish/install QA 5.1.0 through Installed Agents and review its
new provision/read/execute requests. No network or publication grant is implied. Verify the installed
VM's actual browser workload, lost-response resume and exact-source QA result before retrying the
blocked ticket. Source changes and local package verification do not update installed agents or images.

Local acceptance on October 21, 2026: the final affected platform selection passed 677 tests, with
10 optional PostgreSQL cases skipped; all 107 QA tests, its self-test, 10 image/install preparation
tests, four archive-safety tests and the static-server test passed. All five real Chromium fixtures
passed against the final locked image, including screenshots/traces at 375 and 1280 pixels. QA's
package-only build/pack used SDK 5.0.2 with sibling references disabled; the `.nupkg` metadata was
inspected as `CSweet.Agent.QA.VideoGame` 5.1.0. The installed Hyper-V browser workload and live
end-to-end QA callback remain rollout acceptance, not established by these fixture results.

## Chromium-only planning and dependency reporting (October 22, 2026)

Creative Director 2.1.9 `VideoGameCreativeDirectorAgent.SystemPrompt` and `PitchRefinement`,
and Technical Director 5.0.2 `PlanningConstructionGuidance` / `ImplementationBrief.Guidance`
suggest Chromium-only verification for web games. This includes paginated and lightweight
planning. Responsive and input requirements remain; viewport emulation does not establish
real-device certification. Explicit accepted owner requirements still need an authorized
amendment before they can be changed.

QA 5.2.2 `GameQaCriterion.MissingEvidence` separates unavailable checks from passing evidence.
`GameQaExecution.DecisionOutcome` puts those gaps first, retains full report evidence, and
reserves summary space for recovery options. Older reports without the new field prefer
gap sentences over long successful-build prefixes. No result claims other browsers/devices
passed merely because Chromium did.

`WorkBoardService.ToDetailAsync` and `ToItemResponse` return canonical dependency IDs.
`WorkDependencyStatus.UnmetRoots` resolves direct/transitive waits, including missing or
cyclic prerequisites. `WorkBoardWorkspacePresentation.DependencyWait` labels waiting cards
and includes them in attention filtering without changing their workflow or execution state.
`ProjectHealthService.DeliveryDependencyStatusAsync` supplies active-sprint task counts,
excluding managerial personal work. Its current facts participate in owner-report freshness;
stage reports use those counts rather than unverified claims that other delivery continues.
`StageBlockerTextAsync` reads retained QA output so older truncated blocker text can also show
the actual missing browser environment. Previously delivered chat history remains intact.

Regression coverage: `DecisionRequiredTests`, `ImplementationBriefTests`,
`CreativeDirectorLifecycleTests`, `WorkBoardWorkspaceTests`, `WorkBoardServiceTests`, and
`ProjectHealthTests.Dependencies`. Deploy the platform and publish/install the four agent
releases through Installed Agents. Local tests and `.nupkg` files do not update installations.

Producer 4.0.9 `TryReadRetryDirection`, `RetainScopeAmendmentAsync` and `ScopeExpectedItems`
remove amendment-specific text-length gates from command parsing, retained owner direction
and exact replacement descriptions. Concision is prompt guidance; complete source-bound
direction and replacement text remain intact through durable recovery. Empty direction,
untrusted sources, stale scope and unrelated replacements are still rejected. Generic
transport/storage contracts remain unchanged. `DeferredScopeAmendmentTests` exercises a long
multiline direction through capture, restart, reauthorization and planning handoff;
`ScopeAmendmentTests` covers long replacement text without altering the original snapshot.

Local verification: 187 platform regressions passed with one existing skipped workflow test;
agent package-consumer suites passed 118 QA, 155 Technical Director, 213 Creative Director,
and 421 Producer tests. All four self-tests passed, including Producer's published output.
The `.nupkg` metadata, packaged manifests and release-note headings match their final versions.
The Chromium-only owner decision was recorded through Communications and relayed by Naomi
as tracked work for Gabriel. Gabriel acknowledged it and the retained scope-amendment
commitment was observed Running. The scaffold still carried its old criterion at that check;
acknowledgement or a running amendment is not evidence that QA has resumed.

Release rollout on October 22, 2026: the four repositories published their normal GitHub
releases (`v2.1.9` Creative Director, `v5.0.2` Technical Director, `v5.2.2` QA and `v4.0.9`
Producer), each with a Linux `.csab` bundle and SHA-256 file. Installed Agents fetched and
installed each release through its normal update action. All four employee installations
reported `ConfigurationSyncStatus.Current` without a configuration error. Gabriel's new
retained-amendment task ran with package version `4.0.9`; Naomi's relay ran with `2.1.9`.
The earlier `4.0.8` amendment stopped at `NeedsReview` before changing canonical criteria.
The fresh authenticated manager relay created commitment `21c7c197-73d3-409f-be55-f3b4091edc7c`.
It completed exact replacement preparation and queued correction commitment
`fae51e46-1761-444d-bb1f-3ace83cfcca4`; Technical Director `5.0.2` and Producer `4.0.9`
then began the correlated planning proposals. This handoff alone does not establish applied
canonical criteria or resumed QA.
The retained request contains 25 exact replacements totaling 48,710 serialized characters,
exceeding the removed 24,000-character gate. Technical planning retained its 16-item scope
outline and began saving validated three-task implementation-brief pages.

The subsequent correction failed locally after the paginated proposal completed. A read-only
replay against Producer `4.0.9` found generated `work.workflow-ticket.v1` titles being treated
as proposal keys, plus exact-text transcription discrepancies. The 50,846-character amendment
context and 15,630-character correction findings exceeded `BoundPlanningHandoff`'s artifact
bound. Neither canonical criteria nor the QA execution had advanced.

Producer `4.0.10` `RoleRepairArtifact` carries exact replacements and source planning hashes in
`scopeAmendment`; `RoleRepairContext` references that complete artifact instead of duplicating
it. `ProposalKey` excludes generated workflow tickets, and `RefreshAmendedWorkflowPlanningAsync`
applies their explicitly authorized replacements with current revisions and replay checks.
Technical Director `5.0.3` `ReadScopeAmendmentInstruction` and `BuildScopeAmendmentPlanning`
validate the authenticated current-cycle manager handoff and every source planning digest,
then transform canonical planning without model transcription. Completed work, roles, parent
and dependency identities, unrelated criteria, and complete requirement text remain preserved.
No result claims independent QA has passed merely because scope was amended.

The two package-consumer suites passed 424 Producer and 167 Technical Director tests, both
published-output self-tests passed, and `.nupkg`/manifest/release-note versions were checked.
Replaying the live 25 replacements through Producer's 52,228-character artifact, Director's
actual proposal finalization, and Producer's exact coverage validator passed. Both releases
were published and installed normally, Director first. The existing correction commitment
was reopened from Blocked to Ready in Gabriel's Personal Board; no retained state was edited.

Director `5.0.3` then correctly rejected a stale source: ordinary progressive reconciliation
had changed the eight container constraints during the earlier generation. Producer `4.0.11`
permits a fresh authenticated same-direction request to renew a blocked correction using a new
source-turn-specific `ScopeRepairKey`. `RenewableScopeRepairAsync` requires a matching blocked
commitment; `ScopeExpectedItems` must still match each old entry against the fresh board before
reusing edits. The prior snapshots remain immutable. `ReconcilePlanningAsync` holds ordinary
planning while the amendment is Pending/Queued, and `MarkScopeAppliedAsync` records Applied
only after canonical planning and delivery refresh. Five new regressions cover renewal, active
correction refusal, revoked authority, changed originals, planning ownership and idempotent state.
All 429 Producer tests passed, as did the published-output self-test and the current-source replay
through Director finalization and Producer coverage validation. Producer `4.0.11` was published
and installed normally; the identical Chromium direction was relayed anew through Naomi.

The first renewed relay was rejected as `memory.recall_stale` before command application;
a fresh manager relay created a current Pending amendment. Its application then exposed a
contract failure: source-turn-specific snapshot keys exceeded the 128-character
`decisionFingerprint` field when copied verbatim. Producer `4.0.12` hashes those fingerprints
and bounds planning-state mutation keys, preserving full amendment text. The deferred amendment
fixture now enforces host state-key/fingerprint bounds, and a planning-cycle regression includes
the revision-safe merge attempt suffix. All 430 Producer tests, the published-output self-test,
and package version checks passed. The release was published and installed normally; requeueing
the retained commitment successfully persisted fresh snapshots and queued the new correction.

Producer `4.0.12` applied the amendment to canonical task #11 at 17:50 UTC. Its planning
and delivery criterion now requires Chromium alone. Recovery cancelled the stopped first
sprint and created a planned successor, but ordinary planning then replaced the amendment's
planning evidence. Producer `4.0.13` keeps ownership of applied amendment planning until its
anchor finishes and creates an idempotent continuation when the handoff fingerprint drifts.
All 430 Producer tests and the published-output self-test passed; the published release was
installed through Installed Agents and reported Current. Its continuation restored the
planning handoff and requeued readiness without changing the completed implementation.

`WorkDeliveryService.ReconcileCurrentPlanningAsync` adds the explicit paused-plan
`reconcile-planning` control for an authorized manager or owner. It retains graph, assignments,
branches and source identities, rejects active writers and promoted scope, validates canonical
task/delivery text, and creates a new scope revision with fresh acceptance executions. Historical
checks remain superseded evidence. `ProjectReleases` exposes this recovery alongside owner
pause/resume controls. `WorkDeliveryService.Stop` is idempotent for unchanged blockers: repeated
worker pulses must not churn plan revisions and prevent optimistic-concurrency controls.
`AgentWorkInbox.ClaimAsync` orders actionable continuations before delivery-change wake hints,
so an existing notification backlog cannot hold coordination replies behind repeated attention
reviews. Hints remain queued for eventual current-state discovery. The inbox and recovery suite
passed 86 tests after adding the scheduling regression.

`IntegratedCandidateResubmission.ResumeCarriedCandidateAsync`, called from
`WorkOrchestrationService.StartAsync`, recognizes a cancelled sprint's stopped quality candidate.
It revalidates the exact integrated branch, receipt, publication, current plan and independent QA
assignment before queueing fresh QA. An advanced branch or invalid binding stops recovery;
neither earlier QA nor a planning amendment counts as a passing verdict.
`CreateStageExecution` assigns its `ItemExecution` navigation immediately, so candidate recovery
can validate the newly constructed graph before persistence. The carryover regression now uses
this factory instead of manually supplying that relationship; all 15 candidate recovery tests pass.
`WorkOrchestrator.AdvanceAsync` also checks carryover at the initial queue-to-author transition,
before dispatch, because the game profile begins at `ready`. Carryover is restricted to traversal
zero with no author attempt; real QA-requested rework remains executable. The initial-queue,
exact-candidate and hierarchical runtime regressions passed 36 tests.
`WorkOrchestrationBoardState.SynchronizeAgentCard` clears obsolete `BlockReason` text when the
current stage is queued or running, including cards already in the correct column/status. This
prevents the old six-browser decision from appearing as a current blocker after recovery. The
combined dispatch, candidate and hierarchical runtime suite passed 60 tests; its legacy fixtures
now supply the authoritative stage cursor and a persisted runtime for inbox claims.

The owner explicitly approved pause/reconcile/resume. The normal Delivery controls completed
that sequence against scope revision 2 at 18:21 UTC. The active plan then displayed zero current
blockers, three historical checks, and remaining scoped work awaiting independent acceptance.
The focused platform recovery suite passed 53 tests; the earlier broader delivery suite passed
120 with two skipped. This plan recovery alone does not establish a running sprint or passing QA.
Gabriel completed readiness and preflight and started Production Sprint 2 at 18:36 UTC. Its
first task execution is `6b2281de-119f-4e5c-aa3b-d3f377013e1e`. The initial queue dispatched an
author attempt before the queue-transition fix; the owner direction was posted on #11 to preserve
`f36a9de600429432644766aca9ae4a1f9d268299` and return it unchanged for fresh QA. That author attempt
alone does not establish resumed or passing independent QA.
At 18:53 UTC Adrian explicitly reported no source edits and reused the sole original publication
`a44d755e36ff3edcf2ae310567f6c8177cd9905b`. A fresh technical review began at 18:54 UTC. Its completion,
task integration and the new independent QA attempt must still be verified separately.
At 19:08:15 UTC Rohan `5.2.2` claimed fresh quality attempt
`4e13480b-441c-42d4-8537-f034e3718a70` in the new item execution. Its integration-stage output
binds `f36a9de600429432644766aca9ae4a1f9d268299`; the sole source publication remains the original
`a44d755e36ff3edcf2ae310567f6c8177cd9905b`. The project UI shows Production Sprint 2 Active, #11
Running under Rohan, Current stage Quality, and the Chromium-only outcome at planning revision 3.
This verifies QA resumed, not that it passed. Explicit reconciliation also rejects a Pending
promotion; all seven reconciliation regressions passed after adding the in-flight promotion case.
The final API Rebuild started the updated process at 19:11:33 UTC. The same QA attempt remained
Running afterward, the current task's obsolete `BlockReason` was null, its planning revision stayed
3 with Chromium-only criteria, and scope revision 2 retained zero current delivery blockers.

## Earlier operational UI gaps

The October 21 live recovery used Installed agents to verify QA 5.1.0, Compute to observe Linux
preparation, and the project board's Retry action to resume only the stopped quality stage.
Engineering, technical review and task integration remained completed. The following checks
still required source inspection or read-only database diagnostics rather than clear user controls:

- `Compute` shows Linux readiness but not certified image features such as `browser-testing-v1`.
  Add feature/version visibility and a safe image-upgrade/drain action for already-Ready setups.
- Employee Agent operations shows provided capabilities; Installed agents shows definition-level
  required capabilities. Expose the employee's actual approved required capabilities and project
  compute scope together, including missing approvals or preparation.
- A generic ticket Retry does not explain that it preserves completed stages. Show the stage to
  resume and its exact source identity, prerequisites and pending browser operation before retry.
- Expose browser transfer/test progress, exact source/build hashes, receipts and retained screenshot/
  trace downloads on the ticket. Schema/tooling failures should be distinguished from product defects.
- Source control displays platform-generated `Implement ...` commits, while the scaffold planning
  requires conventional commits. Expose a planning/workflow compatibility decision and authorized
  history evidence to QA; never silently waive that criterion or rewrite accepted history.
- Applying a broker fix required Aspire's AgentHost Rebuild action, not an in-product C-Sweet control.
  Surface platform update/restart requirements separately from agent package updates.
