# Recovery after an executor update

Current follow-up: [corrective delivery lifecycle and agent collaboration](corrective-delivery-lifecycle.md).
The unpublished SDK 5.10.2 / Producer 6.2.5 budget-only follow-up below was superseded
by Work Management Contracts 5.3.0, SDK 5.11.0 and Producer 6.3.0. Use that plan's
rollout and verification register; the historical reporting milestone does not
establish live correction or acceptance.

`WorkOrchestrator.RecoverUpdatedExecutorsAsync` runs during normal sprint reconciliation.
It discovers stopped current stages from durable state, so an offline host or missed update
notification does not lose the recovery opportunity. It does not introduce an agent polling loop.

Recovery requires an active sprint, a stopped current ticket/stage,
and a known infrastructure failure recognized by `IsPackageRecoverable`. The same assigned
installation must be enabled, active and ready. Configuration must be Current, or PendingNextStart with no active runtime so normal dispatch starts the updated package. Its installed
package must be newer than the actual version recorded in `AgentRunLog` for the failed work,
and installed after that attempt stopped. Missing or malformed version telemetry fails closed.
The recognized errors are retryable QA transport failures, the QA operation-generation mismatch and the legacy exact MCP 500
specialist-execution outcome. Product findings, permission failures and approvals do not qualify.

The recovery leaves source, assignment revision, planning, completed stages, attempts and
retained evidence intact. It clears the stopped card's claim and wait state, then records
`stage.executor-update.recovery` once per stage, failed attempt and installed package.
`WorkOrchestrationBoardState.SaveBoardChangesAsync` commits state, event and board notification
outbox together. Normal dispatch and inbox claiming still recheck current project authority,
assignment, grants and capacity. An update is a recovery condition, never a QA verdict.

`ExecutorUpdateRecoveryTests` cover version, tenant, lifecycle, cancellation, retry-budget,
product-finding and idempotency boundaries. `QaBrowserComputeWaitTests` retain the exact
operation generation, owner, source and build fences. `WorkDispatchRecoveryTests` and
`WorkOrchestrationRetryTests` verify normal dispatch and completion still work.

## Prism Break rollout

1. Publish and install QA **5.4.11**, Producer **6.2.4** and Director **0.4.4** through
   published releases and Installed Agents. GameEngineer **4.1.15** and companion Software
   Developer **2.1.14** contain the browser-command receipt correction and release-source guard.
   Restart C-Sweet using the updated platform source/build for executor-update recovery.
2. Re-read Sprint 2 and ticket **VGD7DFCCB217-38**. The expected recovery boundary is the existing
   quality stage `96a6a783-89b3-4c37-8738-4830b285d663`, with implementation and technical review
   completed. Preserve integrated Story commit `00671086190bb795266490748832d7007a0cc855`.
   Confirm the recovery event and a new QA attempt on the installed version. If telemetry is
   insufficient for automatic recovery, use the existing ticket Retry action after verifying
   the QA update. Do not reopen implementation or manufacture a quality pass.
3. QA's journal and browser state bind to the same source and planning across attempts.
   `GameQaInteractionTesting` resumes the corrected pending script; completed exports remain
   evidence rather than new waits. Check the unresolved scene-lifecycle assertion independently.
   A prior test's closure-variable error is not a product defect or a passing assertion.
4. After QA has an accepted verdict, the Producer should inspect existing follow-on tickets
   **39** and **40** before starting them. They overlap native Chromium QA and technical review.
   Use the normal manager planning amendment to retain any distinct acceptance criteria and
   link exact-commit evidence. Do not delete history, silently mark checks passed, or create
   replacement planning/review tickets. Existing Technical Director progressive-planning rules
   already prevent new review-only siblings for the same candidate.

Live verification on 2026-11-05 confirmed `stage.executor-update.recovery` at 18:21:56 UTC,
recording QA 5.4.10 -> 5.4.11 and retained assignment revision 1. The actual QA inference
records report 5.4.11. The existing quality stage resumed without reopening implementation,
technical review or integration. Operations 39-43 completed without infrastructure failures.
Generation 40 retained a failed pixel-sampling assertion; QA corrected that test after
observing caption glyphs over brick centers. Generation 43's retained result has 16 satisfied
checks, no errors, and the same integrated source/build. The final independent criterion
report is still pending; successful script checks are not a completed ticket verdict.
The owner published and installed Producer 6.2.4 and Director 0.4.4, then restarted the
platform. After cold startup, the interrupted reporting lease expired and the normal bounded
retry path dispatched fresh QA work. At 18:37:45 UTC, Rohan's replacement runtime claimed
work `0af2b9ad-a707-4ef8-b908-cf85853d0b1d`; the existing quality stage is Running with
no error and no retry timer. The Producer is Running too. Final QA acceptance remains pending.
Source changes and local packages do not themselves update installed agents.

## Accepted recovery, November 5

After SDK 5.10.0 and QA 5.5.0 were published and installed, the existing quality stage
recovered at 20:06:42 UTC from the actual recorded QA 5.4.11 executor. The platform accepted
both exact criteria at 20:11:42 UTC; ticket #38 and Sprint 2 completed. The first recovered
reporting call used 23,712 input tokens, and the recovered run peaked at
44,410 input tokens across 12 calls. Generation 43's 16 passing exact-build behavioral checks were reused.

The consolidated Story source-preparation ticket #41 and Epic source preparation then
completed automatically. Release build readiness reached a separate current-authority wait:
the Technical Director's project grants covered only a VM without outbound networking.
The owner approved a replacement within the existing one-instance, 2 CPU, 2 GiB RAM,
20 GiB disk limits, with outbound access and the existing approved local preview behavior.
The unused generation-1 offline VM had no executed workloads; normal lifecycle teardown was
confirmed before three project-scoped grant changes were applied atomically through
`ComputeGrantAdministration`. No QA result, candidate identity or approved package was patched.

The 5.5.0 QA follow-up uses one resumable build-transfer operation; existing completed browser and behavioral results retain their source/build/generation fences.

At 18:42:34 UTC the replacement QA attempt failed with `runtime.transport` while preparing its report. The retained 16-check result remains evidence, with no accepted ticket verdict. A verified newer installed executor may supply one recovery even when prior infrastructure failures exhausted the ordinary retry budget. Recovery is still idempotent per failed attempt/package, and a failure on the same installed version does not qualify again. Product findings and permission errors remain excluded.

The required follow-up rollout is SDK 5.10.0, QA 5.5.0, matching core and trusted compute provider code. Use published releases and Installed Agents for QA; source builds are verification, not an installed-agent update.

## Readiness recipe correction

The outbound replacement VM `32c2bb5a-2412-44fd-bff5-84ccdc7a2214` reached Ready and restored dependencies successfully. Generation 7 then failed on the older fixed `npm test` recipe because this accepted static scaffold has no declared test script. Ticket #38 and Sprint 2 remain completed; the stopped increment acceptance workflow is ticket #43, outside the sprint.

SDK 5.10.1 and Technical Director 6.0.5 use optional declared tests followed by mandatory build and exact-source HTTP proof. Declared failing tests remain fatal. The bounded legacy recovery was preflighted against the actual retained script, archive, missing script and restore job. Publish SDK first, then publish/install Technical Director, restart matching core, and use owner/manager `ControlAsync` with `retry-validation` for the existing stopped release. Do not reopen #38, delete its verdict, reset the old failed compute receipt, or add duplicate QA tickets. The new restore observation, validation and local publication must be adjacent as required by `ValidateReadinessAsync`.


## Published readiness recovery verified

On November 5, the owner published SDK 5.10.1, installed Technical Director 6.0.5, and restarted C-Sweet. The installed Technical Director configuration was Current. Updating the executor did not reopen the stopped release: its previous missing-test-script finding remained until the owner-authorized Delivery **Retry validation** action (`ControlAsync`, `retry-validation`) was applied at 21:49:27 UTC.

That retry started a fresh rejection epoch, so it repeated source transfer and dependency restore rather than using the previous epoch's retained restore job. Restore completed successfully; generation 13 returned exit code 0 and exact candidate/archive HTTP proof, and generation 14 published the local preview. The platform accepted build readiness at 21:52:48 UTC and dispatched release quality to QA 5.5.0. The first three QA inference calls completed successfully, using 8,718–9,479 input tokens. Ticket #38, its accepted QA verdict, and completed Sprint 2 remained unchanged. Release quality and later acceptance were still pending at this observation.

## Aggregate QA report failure and correction

At 22:07:18 UTC, release quality stopped without a structured report. Its 49 inference calls consumed 1,166,550 input tokens in aggregate. `SpecialistAgent.ExecuteDeliveryScopeAsync` used a separate aggregate harness without the durable journal, browser tools, smaller token caps or browser-compute continuation available to ticket QA. Early successful calls established execution, not a final verdict; the completed build remains accepted.

QA 5.5.1 routes repository-bearing aggregate reviews through `DeliveryScopeBrowserTools`, `GameQaJournal` and `GameQaFinalization`. Each repository uses an immutable `GameQaInputWorkspace` adapter and the existing approved browser helpers. Whole-candidate report validation retains every repository/commit binding. Aggregate journal identity binds the delivery execution, source, planning, scope and reviewer, while excluding the execution cursor revision that changes on compute callbacks. Aggregate interaction state similarly survives replacement stage identities; Task journal identities and interaction keys remain unchanged.

Aggregate evaluation uses at most 65,536 context and 8,192 output tokens, with 24 evaluation iterations. An early stop or exhausted evaluation receives a separate eight-iteration report phase with only evidence reads and structured submission. The code does not manufacture a verdict, turn failed checks into passes, or accept pending browser work. `WorkDeliveryService.DispatchDeliveryAsync`, `ValidateComputeWaitAsync` and `ReconcileDeliveryAsync` authorize quality `awaiting-compute` outcomes and reconcile current operation state under the existing candidate/project/reviewer/generation checks. A successful compute operation resumes quality; it is not QA acceptance.

Verification: 177 QA tests and 172 platform delivery/inbox tests pass, including actual harness early-stop and iteration-limit cases preserving positive and negative reports, stable identity checks, foreign-operation refusals, and a real encrypted inbox/compute/resume round trip. QA self-test, source-release check, NuGet 5.5.1 metadata and Linux bundle verification pass.

Rollout: publish/install QA 5.5.1 through Installed Agents, restart C-Sweet with the matching platform change, then apply normal owner/manager `retry-validation` to the existing #43 quality failure. This retries quality while preserving accepted build readiness, ticket #38 and completed Sprint 2. Technical Director 6.0.5 and SDK 5.10.1 need no further release for this correction. The live quality verdict and later acceptance remain pending until rollout and actual testing complete.

## Saved-test handoff and reserved report submission

The owner installed QA 5.5.1 and restarted the matching platform. The normal owner retry at 22:49:04 UTC resumed only quality. Generation 44 transferred the exact build in one operation; generation 45 passed Chromium smoke at 1280x720, with canvas/WebGL and no browser errors. Generations 47–49 retained an independent behavioral run. Its scene-active observation was captured inside `create()` before the scene became RUNNING, and brick samples were offset by 42 pixels. QA identified these test defects and saved two corrected drafts, but neither draft executed before its evaluation budget ended. A later report-phase submission included an earlier failed hygiene command despite a successful clean-copy rerun, then ran out of report turns during correction. The platform correctly blocked at 23:05:55 UTC; no verdict was accepted.

QA 5.5.2 adds `DeliveryScopeBrowserTools.ExecuteSavedDraftsAsync` before model evaluation on reconnect and before report finalization. It restores and executes only the latest source-bound authored script that differs from the retained script identity. Existing pending, completed and unknown requests are never reset. Live read-only preflight confirmed the saved correction `qa/browser-21014d1a98744c5da97ff915d2b79361e9e4ccfc62f2fff1f6f6b8d4c10c3eeb.cjs` is content-addressed, 10,299 UTF-8 bytes, and different from the completed first test. No test or operating-state row was patched.

`GameQaFinalization` separates four evidence-reading iterations from up to four reserved structured-report requests. `PlatformChatClient` already carries response schemas but drops required tool-choice settings, so the reserved phase uses the existing supported response-schema transport and invokes the same validated report function in code. It retains the last rejected arguments/feedback and the original four-attempt correction guard. Malformed data, unexecuted drafts and unresolved candidate failures cannot produce a pass. Historical invalid/superseded harness checks remain in the journal and summary; current validations require actual corrected execution evidence.

Verification: 183 QA tests pass, including replacement-workspace saved-draft continuation, pending/unknown replay refusals, cross-execution isolation, evidence-read exhaustion with reserved correction, and the real SDK structured-response transport with malformed-data refusal. The QA self-test, source-release check, NuGet version and Linux bundle verification pass. The matching platform's 172 delivery/inbox checks remain valid and its source is unchanged by 5.5.2.

Publish/install QA 5.5.2 through Installed Agents, then retry #43's quality stage normally. No additional C-Sweet restart, SDK release, permission change or Technical Director update is needed for this agent-only follow-up. Accepted readiness, #38 and Sprint 2 remain intact. The retained correction must execute and an actual report must be accepted before claiming live recovery.

## Corrected browser pass and report-budget follow-up

The owner installed QA 5.5.2. The normal retry at 23:44:40 UTC resumed the saved script without another model investigation. Generation 50 prepared it, generation 51 ran script `21014d1a98744c5da97ff915d2b79361e9e4ccfc62f2fff1f6f6b8d4c10c3eeb` against the unchanged source/build, and generation 52 retained its evidence. The full result has 17 satisfied Chromium checks and zero errors; its evidence archive is 94,810 bytes with hash `7880b7e6168a1909d7f9d9e1eb002fd88365d5ddd49878a0488dfa7a10d701f7`.

QA's later source assessment identified a mislabeled `SAMPLE_POINTS.brickExplosive` export and the corrected test's omission of that point from its assertion set. This is an assessment requiring a structured verdict, not an accepted product finding yet. Report generation failed at 23:55:35 UTC: `qwen3.8-flash-next` exhausted two 8,192-token responses without a usable answer. The preceding report-reading calls reached 86,252 input tokens, exposing repeated raw evidence pages and generic testing instructions in the reporting phase. No report or verdict was accepted; the browser result and historical failed scripts remain preserved.

QA 5.5.3 removes the separate report-reading harness. `GameQaJournal.ReadReportEvidenceAsync` retains bounded real command excerpts, excluding long script and minified-source dumps; complete observations stay in the journal. `GameQaFinalization.Assessment` carries bounded public evaluation text without private reasoning content. `DeliveryScopeBrowserTools.ReportEvidence` supplies the retained exact-build receipts. `GameQaFinalization` uses report-only instructions and the existing structured-response transport, up to four validated submission attempts, concise report fields, and a separate reporting output budget of up to 32,768 tokens within the configured ceiling. Evaluation stays at 8,192 output tokens. An oversized packet is rejected explicitly rather than silently dropping required criteria.

Verification: 184 QA tests pass, including the real SDK response-schema/output-budget path, malformed-response refusal, preservation of actual failed diagnostics during compaction and exclusion of large minified-source data. Self-test, source-release check, NuGet metadata and Linux bundle verification pass. Publish/install only QA 5.5.3, then retry the existing quality stage. No platform restart, SDK update or other agent update is required. The completed 17-check result must be reused, and an actual structured report still must be accepted before declaring recovery or promoting the increment.


## Accepted aggregate verdict and manager handoff

The owner installed QA 5.5.3. Normal Delivery `retry-validation` at 2026-11-06
00:15:46 UTC (November 5 Pacific) resumed the existing #43 quality stage. At
00:22:50 UTC the platform accepted its structured **changes_requested** verdict.
`GameQaFinalization` reused the retained 17-check browser result and produced a
report with 15,790 input and 17,540 output tokens. Earlier report-reading calls
had reached 86,252 input tokens; an 8,192-token output ceiling had prevented a
usable report. This is a recovered reporting path, not product acceptance.

Open finding `f178bf24-160c-4fac-a357-b30be464ba13` binds to candidate digest
`122142436f1e1dcefc8f646536c2a7b2e54fba67f50c08d6307262f2cce94ad8`:
`SAMPLE_POINTS.brickExplosive` in shipped `src/placeholder-scene.js` selects the
purple tough brick (139,92,246), while `SAMPLE_COLORS.brickExplosive` documents
red (239,91,91). The corrected browser script omitted that point from its
assertions. Six clean-copy repository checks passed; historical hygiene and
symlink failures were superseded harness issues. Preserve those observations
and the accepted negative verdict; do not retry simply to erase a source finding.

QA also lacked Technical Director compatibility/package evidence because the
activated recipe schedules quality before technical review. SDK
`HierarchicalProjectDelivery` 5.10.2 orders newly planned release stages as build
readiness, technical review, independent quality, manager review. Activated
workflow graphs remain unchanged; the existing finding and backward dependency
require normal manager-controlled corrective planning and fresh validation.

The subsequent Producer 6.2.4 `manager-incident` inference returned no usable
answer after correction under its 1,600-token request. Its factual fallback
retained the incident, but no corrective task was created. SDK
`ManagerIncidentCoordinator` 5.10.2 gives report and authenticated recovery calls
up to 32,768 output tokens and a five-minute deadline, still constrained by the
installation ceiling. Existing routine-tool allowlists, decision/source fences,
stable operation keys and concise report validation remain enforced. Producer
6.2.5 consumes the packaged SDK. This change does not grant new authority or
resolve the finding.

Verification: 722 SDK tests, two sample tests, seven generated-template tests,
505 Producer tests, and 266 platform consumer tests pass (one opt-in live-provider
test skipped). SDK NuGet metadata is 5.10.2. Producer self-test, source-release
check, NuGet 6.2.5 metadata and Linux bundle verification pass; its bundled SDK
DLL matches the tested 5.10.2 package exactly.

Rollout: publish SDK 5.10.2 first, then publish/install Producer 6.2.5 through
Installed Agents. QA remains 5.5.3 and Technical Director remains 6.0.5. No
additional platform restart is needed for these agent-side helpers; the core
SDK pin is synchronized for its next normal build. After installation, verify
that the retained incident leads to real corrective work and independent
validation. At this checkpoint #43 remains blocked by its accepted source
finding, #38 and Sprint 2 remain completed, and Sprint 3 remains planned.
