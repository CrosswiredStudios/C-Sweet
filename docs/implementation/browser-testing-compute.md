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
attempt, bound to an authorized source commit, reviewer-owned project environment, current operation
generation and bounded recovery time. Code QA uses its source-control workspace. Artifact-report authors
use the immutable `WorkExecutionAssignment.Candidate` in their protected inbox assignment instead:
`ValidateArtifactComputeSourceAsync` verifies the current sprint/stage/traversal/attempt, completed inbox
handoff, assigned active identity and task read access. `WorkArtifactRepositoryInput.AuthorizeAssignmentAsync`
revalidates active plan/scope, planning/staffing/dependency digests, exact Story branch pins and current
repository access, shared with `ReadTaskInputAsync`. A completed handoff does not need a live inbox lease;
archive reads still do. No writable workspace, publication or approval is synthesized. It retains the current quality stage without recording a pass,
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

## Artifact QA browser-wait incident (2026-10-22)

Prism Break #16 failed at `AcceptComputeWaitAsync` after #11–15 completed because the browser
compute feature only searched `SourceControlWorkspaces`. #16 correctly delivers an Artifact report
with a pinned read-only Story snapshot and therefore has no writable workspace row. Browser compute
support (commit `6733188f`) added this code-ticket assumption after artifact repository inputs
(commit `80d38996`) already existed. Separate snapshot-read and code-browser tests missed their
asynchronous combination. The source rejection was a platform contract mismatch, not a product defect
or an agent choosing an unauthorized commit.

`ArtifactRepositoryInputTests.SavedArtifactInputIsNarrowAndReauthorizedOnEveryRead` now also completes
an inbox handoff and admits the browser wait without any workspace. It rejects wrong source/attempt/
owner/traversal and changed scope, planning, staffing, repository access or dependency receipts. The
existing `QaBrowserComputeWaitTests` retain code-ticket, compute generation/ownership and verdict checks.
Recover existing failures using the board's Retry after rebuilding the API; preserve historical attempts
and let the assigned QA resume its source/planning-bound journal. Never convert a browser wait into a pass.

Live verification on October 22 at 17:09 Pacific: the API was rebuilt through Aspire and #16 retried
through its normal board action. Seven new `awaiting-compute` results were accepted, compute operations
completed and resumed automatically, the original rejected attempt remained in history, and the active
sprint retained five completed tickets with no current failed/blocked ticket. The final QA verdict was
still pending. Validation: 30 artifact/browser authorization tests and 140 activity/delivery runtime tests
passed; no agent packages or installed payloads changed.

## Provider intake failure and Ready-card correction (2026-10-22)

At 17:11 Pacific the Qwen Games provider logged `dispatch-intake-stopped` for #16's build-upload
operation `c1b1499e-2e41-40c0-9954-b4aec4300c96` (generation 278). The protected journal contained the
execution claim but no result; Core remained Dispatching. `ComputeProviderMaintenanceSupervisor`
kept local lease enforcement alive, but discarded the intake exception and never resumed that lane.
Windows therefore continued to report Running while QA's five-minute bounded recovery kept reading
the same unfinished operation. The original exception details were lost, so its exact trigger cannot
be established from retained evidence. This is distinct from the earlier source-snapshot admission bug.

`ComputeDispatchIntakeWorker` now supports host-selected bounded recovery of unexpected failures and
reports the exception type without raw guest data. `HyperVMaintenanceHost` enables it. Recovery always
rediscovers and obtains fresh Core authority; previously claimed operations receive Observe authority,
and the protected journal still forbids duplicate physical execution. Lease enforcement continues.
`ComputeIntakeSupervisorTests` covers recovery after a fenced effect and verifies one physical claim.

The protected runtime updater now supports an explicitly selected business through `repair-local`,
validates its existing enrollment, re-certifies the unchanged image/runtime via the installer and
retains rollback copies. Qwen's update receipt completed at 17:46:14 Pacific with configuration and
workloads preserved. Observation closed the old operation as `outcome-unknown`, without replay.

QA 5.2.3 (release commit `74295c5`) adds upload-only recovery: a read-only probe hashes the exact
retained part. Matching bytes allow continuation; a matching incomplete prefix permits a guarded write
of the same bytes, with the prefix checked again inside that operation. Conflicts stop recovery, two
probe cycles bound it, and unknown browser tests never take this path. Full QA suite: 122 passing tests,
self-test and verified Linux CSAB; installed via Installed Agents. The live probe found zero bytes at
the uncertain upload offset, confirming the chunk was missing before the guarded repair was submitted.

`WorkOrchestrationBoardState.SynchronizeAgentCard` previously mapped every Backoff/Pending stage to
Ready. A current `awaiting-compute` continuation with no error now retains its execution column and
Running ticket status, with `WaitingReason` distinguishing compute wait from queued QA continuation.
This does not mark an idle agent runtime as executing. Real retry failures retain ordinary Ready
behavior. `WorkDispatchRecoveryTests` and `CurrentActivityTests` cover the distinction; the combined
board/activity/intake regression run passed 121 tests. The API was rebuilt at 17:49:59 Pacific.

At 17:52:46 Pacific Rohan 5.2.3 entered a fresh Running/Generating QA assessment after the recovered
Chromium smoke operation `b7df639b-71a3-4e9f-82ee-0b4bd9cb1192` completed with `passed=true` for
source `92d8bf7004539337b89a05bf8d2848b22f14f2a3` and build
`2b450143c10104e9c242b6809e9563bfd0517baa207107625aee5c4431d06ba7`. Its 21,939-byte evidence
archive was retrieved before assessment resumed. The live board showed #16 In Progress, Ready empty,
Blocked empty, and Sprint 2 still Active with five of six complete. This proves recovery through the
failed boundary; it is not a final QA verdict. Final browser screenshots from the product UI could not
be captured because Chrome's capture command timed out; UI accessibility state and authoritative
execution records both confirmed the corrected status.

## Saved QA report lost behind closing-response failure (2026-10-22)

After the recovered browser test, #16's attempt 42 completed its assessment and durably saved a
validated report at 18:10:51 Pacific. The harness then requested optional closing prose. That request
was cancelled during transport, and `GameQaExecution.ExecuteCapabilityCoreAsync` converted the
resulting `HttpRequestException` into a successful Blocked outcome. The ticket therefore claimed no
verdict had been accepted although its report checkpoint existed. The attempt deadline had not
expired. Retained diagnostics establish this ordering; they do not identify the original network
failure's precise trigger.

QA 5.2.4 (release commit `482bddc`) makes the successful durable report checkpoint terminal for the
model phase. `GameQaReport.CorrectionBudget` accepts the report only after its checkpoint succeeds;
source integrity, current planning and artifact submission still execute afterward. Outer shutdown
cancellation cannot be mistaken for report completion. Transport errors without a completed report
return classified technical failures for bounded platform recovery instead of a product decision.

`GameQaJournal.ReadResumeReportAsync` also permits recovery from the immediately preceding exact
stage/attempt's legacy HTTP processing failure. Its existing identity binds the source files,
planning, scope and assignment revision; ordinary retries, changed authority and product failures
cannot reuse a report this way. New regressions cover terminal harness behavior, failed checkpoints,
shutdown, transport classification and the narrowly authorized recovery. All 130 QA tests, self-test,
Linux publish and CSAB verification passed. Release v5.2.4 was published and installed through
Installed Agents at 18:33 Pacific before the normal board retry created attempt 43.

Live recovery at 18:34 Pacific: attempt 43 used the retained report and submitted artifact
`990d9d35-a028-4b0a-8b67-6ac16c1e0cb6`, revision `4250ab65-10e5-4b48-8f01-781b129d32ee`,
SHA-256 `0171213ce2c630b284410de365afc641fc42cccddbb659f2aedb1f7e0b31c6df`, in six seconds.
No new QA assessment model call was made. The platform accepted the exact current author outcome
and dispatched Gabriel's independent `quality` review at 18:34:37 Pacific. QA installation 5.2.4
reported configuration Current. This establishes successful recovery through artifact submission;
independent review and sprint closure remain separate gates.

## Behavioral evidence gap exposed by independent review (2026-10-22)

At 18:44 Pacific Gabriel correctly returned `changes_requested`: the saved report claimed all
four criteria passed while acknowledging that audio used a mock and scene/layout checks were
structural. The browser workload proved startup only. Recovering the report fixed finalization,
but did not establish the missing behavior. The existing prompt warned against that substitution;
the available browser tool still offered no way to execute the required interactive assertions.
Independent review prevented that insufficient report from closing the ticket.

QA 5.3.0 `GameQaInteractionTesting` adds `test_web_interactions` after the exact-build smoke test.
It uses existing approved compute command callbacks and the already certified browser image,
without a direct Docker connection from the agent runtime or new grants. Fixed container arguments
retain network isolation, read-only inputs/root, resource limits and a self-terminating budget.
The authored Playwright script executes against the immutable production archive. Its source,
build and script hashes, environment, operation generation and literal request survive callbacks;
lost submission replies reuse identical terms and unknown tests never replay blindly.

`qa-interactions.cjs` records named boolean assertions and browser errors, and rejects an empty
assertion set as a pass. `qa-interactions.py` reuses the prepared image's safe digest-checked archive
extractor and packages the exact test, screenshot, trace and result. Transfers verify the evidence
ZIP digest before resuming assessment. Prompt/tool guidance requires observed browser behavior
for behavioral claims; missing required checks remain unverifiable rather than nonblocking notes.

The 136-test QA suite includes before/after-acceptance lost replies, multiple focused scripts,
unknown execution, changed source, confined paths and exact-result identity/consistency checks.
`scripts/test-browser-interactions.py` passed five real Chromium fixtures: live input, real Web Audio
and portrait behavior, plus deliberate assertion failure, page error, blocked external request and
zero assertions. Fixture archives include validated hashes, scripts, screenshots and traces, and
the containers were confirmed removed. Self-test, Linux publish and CSAB verification passed.
Release commit `5c254c5` published v5.3.0 and Installed Agents installed it at 18:59 Pacific.
The ticket discussion relayed the new tool and required re-verification to Rohan at 19:00 Pacific.

At 20:05 Pacific, retained commands showed concrete draft progress rather than identical-command
polling: audio, scene and viewport scripts were being authored. A shell policy falsely matched
`shutdown` in heredoc JavaScript; the draft was corrected and written. Independent execution of
those exact drafts against the retained production archive exposed test-harness mistakes:
`game.scene.on` is not a Phaser SceneManager API, `page.evaluate` accepts one serialized argument,
and native OscillatorNode does not supply the assumed `state` property. The audio probe nevertheless
measured a non-silent waveform (RMS 0.7173). These observations were posted to the ticket as test
corrections, not game defects or a QA pass.

QA 5.3.1 (commit `228e634`) adds `GameQaBrowserScripts`: a confined content-addressed draft save tool
and source-bound draft restoration. Small legacy heredoc drafts are recovered as text from the
integrity-checked command journal, without shell replay or original-source writes. A durable compute
wait now cancels only the model phase immediately; it grants no verdict. Focused corrections no
longer hit a four-script stage gate. Journal, workload and workflow bounds still apply.

The first live interaction request exposed an error in 5.3.0: `GameQaInteractionTesting.RunCommand`
requested 32 KiB of stdout, while `ComputeMcpTools` permits 8 KiB. Guest command validation allows
more, so the agent-only fake and direct container fixtures missed that narrower callback contract.
No test operation was admitted. QA 5.3.2 (commit `0eb807f`) returns a compact control receipt and
transfers the full assertion report inside the hash-verified evidence ZIP. It checks exact identities,
verdict and assertion/error counts before assessment. Recovery requires the immediately preceding
exact stage's authoritative schema rejection and no accepted operation; new request identities
refresh the wrapper files before testing. Accepted and unknown operations cannot use that recovery.

Verification: 143 QA tests, six actual platform SDK/MCP contract tests, self-test, verified Linux
CSAB and all five real Chromium fixtures passed. Fixture control receipts remain below 8 KiB and
retain the full report in the archive. Installed Agents installed 5.3.2 at 20:22 Pacific, then the
board's normal Retry resumed #16. Source, criteria and previously completed tickets were preserved.

That recovery exposed a second rejected request: its derived idempotency key exceeded the callback's
128-character limit. QA 5.3.3 (`5aaec1d`) hashes structured interaction scope into a fixed 68-character
key and narrowly replaces the exact unaccepted legacy request. The evidence reader also uses the
guest-required absolute executable path. Exported production prepare/run/evidence requests, including
a near-limit script, now pass the actual platform MCP schema and guest validator (seven contract
tests). All 146 QA tests, self-test and Linux CSAB verification passed; Installed Agents installed
5.3.3 at 20:45 Pacific. Neither schema rejection admitted a browser test operation.

Independent diagnostics executed the exact retained production archive
`2b450143c10104e9c242b6809e9563bfd0517baa207107625aee5c4431d06ba7` in Chromium
156.0.8078.4. Correct native Web Audio sampling passed 14 assertions (1.3-second measured crossfade,
eight distinct SFX, silence between slots); actual Phaser scene lifecycle passed 12. Layout failed:
390x844 and 412x915 displayed the portrait overlay and paused the game, but trusted clicks reached
the Menu scene; landscape resume then entered Gameplay from queued input. The production
`OrientationGuard._setOverlayVisible` left `pointerEvents = 'none'` while visible. This is a genuine
implementation defect, reported to Gabriel, not a test-script API error. Diagnostic runs are not a
replacement for the assigned QA report or its independent producer acceptance.

`WorkOrchestrationService.CreateLinkedDefectAsync` addresses the missing corrective Bug path.
The release manager/owner needs current board recovery/create and project configuration grants.
It requires current planning/staffing pins, the exact trusted integration receipt, drained active
sprint work, stopped artifact QA in the same Story, no accepted downstream items and no aggregate
candidate/promotion. A serializable transaction preserves the completed implementation and receipts,
creates a registered Bug with the same criteria/constraints and independent staffing, adds it to
the Story's canonical evidence/predecessor graph and sprint, and makes the QA task depend on it.
The QA task gets fresh planning/assignment revisions and a stopped author traversal; old evidence
cannot pass the new source. Only unstarted aggregate executions are superseded. Replays bind actor,
source/QA revisions, title and complete reproduction text; current authority is rechecked.
`WorkOrchestrator.ManagerDirectionsAsync` supplies the authorized defect to the fresh engineer.
Normal review/integration/QA still apply. `WorkBoards` exposes the linked Bug action. All 92 focused
defect, recovery, dependency-input, workflow, UI and actual compute-contract regressions passed.

The owner filed registered `video-game.bug.v1` **VG8FCF1B614B-20** through the new UI at
21:16 Pacific. Adrian claimed its author stage; #15 stayed Completed, #16 remained Blocked with
the explicit #20 dependency, planning revision 4 and assignment revision 3. The active sprint
correctly changed from 5/6 to 5/7; the delivery plan advanced to scope revision 3. The actual
failing Chromium script was posted on #20, and native audio/scene reference scripts on #16.
Those scripts still require execution against the newly integrated source before acceptance.

QA **5.4.0** (`3db18df`) fixes another handoff gap: the execution harness did not expose a current
discussion reader despite instructions to read dependency comments. `GameQaDiscussion` now offers
`read_task_discussion`, restricted to the assigned item and declared dependencies, using fresh
broker authorization and pages of 20 complete comment bodies. It neither truncates bodies nor
treats discussion as execution authority or validation evidence. All 148 QA tests, self-test,
Linux publish and CSAB verification passed. The release workflow succeeded and Installed Agents
installed 5.4.0 at 21:21 Pacific. No new grants or network access were requested.

Post-deployment defect regressions also preserve a 16,000-character reproduction and deny
idempotent replay after the create grant is revoked. All eight cases passed in the separate
`Qa16DefectVerified` configuration while the live API and engineering execution continued.

## Single-operation evidence archives

`ComputeEvidenceArchive` is a dedicated `compute.execute.v1` workload binding source commit, build digest, optional authored-script digest, evidence SHA-256 and exact byte length. The archive path is derived from those identities. `ComputeEvidenceArchiveTransfer` streams bounded 256 KiB guest pipe reads inside one provider operation, refuses symlink components and non-regular files, and verifies length and incremental SHA-256 before publishing any bytes. The total archive remains capped at 4 MiB with a 30-second deadline. Ordinary command output remains 8 KiB. No guest image change or network grant is required.

The existing provider replay journal/outbox retains the complete verified result atomically; signed result envelopes admit the larger bounded archive only for artifact results. `ComputeResultReconciler` rechecks the original request identities, length and hash before committing the artifact and completion wake. `ComputeBroker.OperationView` omits binary content. The operation ID is the artifact handle; authorized `ReadEvidencePageAsync` reads require current organization, installation and environment access. SDK 5.7.0 `ReadEvidenceAsync` performs 64 KiB reads within one callback and validates the final hash, so archive size never creates agent-work callbacks. Reads can restart after cancellation without replaying a test. Unknown export outcomes have bounded read-only recovery; unknown browser executions remain fenced.

QA 5.4.9 `GameQaEvidenceTransfer` checkpoints the one export request before submission and reuses its stable key after a lost reply. Both smoke and behavioral checks use it. Already-retained legacy chunks and pending chunk requests remain untouched; an in-flight legacy read finishes before the archive export. Full behavioral assertions are parsed and compared with the original receipt before a verdict. `GameQaJournal` retains the archive handle/hash/length instead of duplicating binary ZIPs into many operating-state records. No product acceptance is synthesized.

Verification includes `ComputeEvidenceTransferTests`, `ComputeRuntimeUpdateScriptTests`, SDK `ComputeEvidenceTests`, and QA `BrowserRecoveryTests.Pending_legacy_chunk_survives_upgrade_and_exports_remaining_evidence_without_replaying_test`. The optional `CSWEET_EVIDENCE_PROBE_EXPORT` test environment variable exports an actual reader request; `tests/compute-evidence-read-probe.py` runs it in an offline disposable Linux container and checks regular-file/size and symlink boundaries. `Install-ComputeLocalProvider.ps1` uses `Install-ComputeProviderRuntime` during retained-service recovery to replace only the stopped service payload, validate it, and roll back on failure. The installer runs current component acceptance and uses `repair-local` to certify the new runtime against the retained image before validation. Runtime and certification metadata roll back together on failure. Enrollment, signing keys, images, journal and workload disks remain in place.

Owners can request a retained runtime update from `ComputePreparation` through `ComputeDashboardEndpoints.QueueRuntimeUpdateAsync`. The action requires Ready setup, current owner membership, and no outstanding compute operation, audits the request, and enters the normal preparation/UAC flow. It does not release or recreate existing instances.
