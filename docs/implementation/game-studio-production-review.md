# Bounded production-brief correction and source evidence

## Sprint 2 execution, review weight and candidate handoff

Producer 6.2.2 `CorrectableDeliveryRetry` permits one ordinary revision-bound
manager retry for the exact legacy MCP-500 Blocked outcome on an active assigned
implementation stage. Its stable stage key deduplicates wake/reconnect delivery;
a repeated failure, QA outcome, denied grant or exhausted attempt allowance is not
automatically retried by this path. Install Engineer 4.1.13, QA 5.4.10 and Technical
Director 6.0.4 before Producer so the retained-stage recovery uses the corrected
implementer. Daniel 2.1.12 contains the same shared developer-core changes.

`IncidentReports.TransportIncidentReport` supplies a deterministic, current-fact
investigation owner and next action for stopped MCP work. It does not claim a
service repair, runner-capacity diagnosis or executed retry. `ReconcileEstimatesAndQaReadinessAsync`
starts all requested role estimates, then waits for all of them before producing
the exact candidate readiness request. Partial estimates no longer generate
successive subset QA assessments.

Technical Director `PlanValidationIssues` no longer requires a separate QA leaf
in every generated plan. `PlanningAuthorityPolicy`, `PlanningConstructionGuidance`,
the worked example, correction prompts, `BuildRoleRepairPlanning` and progressive Story refinement now use
the Code task's independent technical-review and quality stages for routine checks.
A standalone QA/technical-review task needs a different deliverable unavailable
from those stages; the example uses participant observations. Accepted product
requirements and independent review remain mandatory. Published tasks and frozen
scope are not deleted or rewritten by an upgrade; consolidating already published
review siblings uses the normal explicit manager planning/amendment flow.

QA `BrowserAcceptance.Validate` prevents a passing browser-visible report until
`GameQaBrowserTesting` has completed and retained actual browser evidence for the
same source identity and commit. Canvas criteria need observed canvas evidence;
they do not implicitly require WebGL. `BrowserAcceptance.Handoff` names the exact
candidate and actual Story target. Historical prose about main does not select
another snapshot; an explicit final-main promotion criterion is still downstream
evidence. Browser defaults follow accepted viewport scope, avoiding invented mobile
or renderer commitments for a desktop-only scaffold.

Regression entry points: Producer `DeliveryRecoveryTests` and `IncidentReportTests`,
Technical Director `LeanReviewPlanningTests`, `RoleRepairPlanningTests` and `ProductionPlanningTests`, QA
`BrowserAcceptanceTests` plus existing browser, exact-input and recovery coverage.
The developer-core/browser implementation and real-browser verification are mapped
in `developer-execution-workflow.md`.

## Entry points

- Producer `SpecialistAgent.RefinePitchAsync`, `CanCorrectProductionBrief`, and `EnsureProductionSourcesAsync`.
- Director `ReviewProducerPitchAsync`, `CorrectionFor`, and `VerifyProductionSourcesAsync`.
- Creative Director `ReviewCreativeEvidenceAsync` and `CreativeChecklist`.
- Publisher-owned `ProductionReviewContracts.cs` is identical in the three agent repositories; it adds private coordination fields without changing SDK or platform contracts.

## Correction policy

The ordinary Producer budget remains four drafts. An authenticated manager reply bound to the exact ready, question-free Producer revision can provide a `ProductionCorrection` receipt. At most two extra correction receipts are consumable per existing owner-decision window. Duplicate receipts do not consume or reset the allowance. The second needs a changed candidate, explicit accounting for every previous blocker, and an evidenced resolution of at least one blocker. Unchanged objections, foreign replies, stale candidate hashes, and repeated unresolved questions remain blocked. Exact accepted handoffs remain consumable after exhaustion.

Director `CorrectionFor` preserves existing concrete legacy review changes as stable candidate-bound findings, permitting a bounded correction on retained work. That does not reset budgets or waive independent review. Exhaustion returns a specific manager-recovery explanation; it never creates spending, staffing, scope or launch authority.

## Review checklist and finding history

`CreativeChecklist.Version` is `production-brief.v1`. All six categories must be assessed: scope, player-loop, controls-accessibility, presentation-audio, validation-plan, and constraints-non-goals. Issues have stable IDs, a cited requirement, current evidence, severity, open/resolved status, responsible role, due milestone, and corrective action. Advisory-only results must be accepted observations; open blockers require rework or escalation. `RequiredChanges` must exactly match open blocking actions.

Every previous blocking issue must retain its identity and explicit disposition, including resolved issues. The ledger is document/pitch scoped, reads authorized current revision history, and cannot move backward when an old review is replayed. Newly generated reviews require the checklist; previously completed legacy receipts remain readable and are not rewritten. Requirement interpretation and truthfulness of model-authored evidence still require domain judgment; structural validation alone does not prove creative correctness.

## Compact documents and exact downstream evidence

The Producer stores the exact accepted pitch/GDD once per project/team/source pair in a project-owned `video-game.production-sources.v1` document. Source metadata retains original identities, types, accepted revisions and hashes; original text stays readable as Markdown with length-delimited parsing. This avoids mixing an intake-owned pitch into a project-scoped package or expanding grants.

Each working brief carries only an exact source reference. The Director rereads both accepted originals, checks source identities against the project's accepted pitch and GDD, compares full contents, and accepts the source document only as verified provenance. Independent creative review receives both the working brief and source evidence. Before staffing, the Producer requires both exact revisions accepted and packages both for existing downstream readers. No Technical Director adaptation is needed: the ordinary package path reads each accepted member, and paragraph-based source coverage still sees the original Markdown paragraphs.

Historical briefs remain intact. Their original source appendices are removed only from working model context, never from stored historical revisions. New source references and old appendices are stripped from the editable body before revision so they cannot accumulate. The short-brief character target is guidance, not a substitute for complete accepted scope.

## Release and recovery

Coordinated versions: Producer 6.2.0, Creative Director 4.1.0, Director 0.4.0. Publish and update all three through Installed Agents before resuming an existing production collaboration. No capability grants, SDK/contract package changes, database migration, or stored-business repair is required. A blocked session still needs its ordinary authorized manager recovery/resume; preparing a local bundle does not resume Cool Games.

Regression entry points: Producer `PitchRefinementTests`, `ProductionCorrectionGuardTests`, `ProductionSourceFormatTests`; Creative Director `CreativeChecklistTests`; Director `ProductionSourceTests`; platform `CreativeHandoffJourneyTests`, which exercises actual Director/Creative callbacks for legacy and separate-source handoffs with deterministic model responses. These are fixture-based behavioral tests, not a claim of a live-model or live-business recovery.

## Director upgrade recovery (0.4.1)

Live inspection after the three updates confirmed Director 0.4.0, Producer 6.2.0 and Creative Director 4.1.0 active. Cool Games' original production session was still Blocked at revision 19. Director attention repeatedly failed in `CheckProducerHandoffAsync` → `RequireProgressingHandoff` before any new coordination callback could reach `CorrectionFor`. Resuming alone also needed a callback route past the historical Producer-blocked response. This was a missing lifecycle recovery path, not a failed installation.

`RecoverProducerCorrectionAsync` now recognizes only the retained, ready, question-free candidate stopped by the legacy draft-limit message with an unstructured manager reply. It verifies current session ownership, project/team, accepted original sources, latest submitted candidate and cached exact independent rework. Pending/paused owner decisions, cancelled sessions, native turn limits and existing structured correction receipts prevent recovery. A persisted `ProducerCorrectionRecovery` marker binds the expected session revision and candidate before the existing SDK resume operation. Unknown resume responses use current-state discovery and the same idempotency key; a later blocked revision cannot reset the allowance.

`ResumedCorrectionReviewAsync` requires that marker, the resumed revision, current scope and unchanged latest submitted candidate before the Director emits the retained creative correction. The existing Producer correction guard still owns its bounded edit allowance; staffing and acceptance are unchanged. No business records, historical turns, approved manifests or installed payloads are rewritten.

Regression tests: Director `ProducerCorrectionRecoveryTests` covers the saved failure shape, replay, lost responses before/after commit, stale queued candidates, and authority/decision/turn-limit rejection. Platform `ProducerUpgradeRecoveryJourneyTests` executes actual Director and Producer callbacks through recovery and one compact revision without registering model, hiring, spending or acceptance capabilities. Publish/update only Director 0.4.1 to enable this path with the already installed Producer 6.2.0 and Creative Director 4.1.0. Live continuation remains a separate check after that update.

## Planning direction, runner setup and prerequisite continuation

Producer `EnsurePlanningSessionAsync` now requests the private `Direction` phase when a frozen progressive scope exists. Technical Director `GenerateProgressivePlanningAsync`, `ReviewPlanningQuestionsAsync` and `ApplyRetainedPlanningDirectionAsync` load that exact scope, assess indexed questions once, and retain a digest-bound direction overlay for future Story refinement. The output keeps the original graph, criteria, coverage and scope digest. A direction requiring an amendment blocks explicitly; it cannot silently edit dependencies. Routine delegated work, accepted-source status and deferred optional work do not become management approvals. Classification still requires domain judgment; code validates exact question coverage, ownership, dispositions and immutable scope, not the truth of every model explanation.

Live diagnosis: the original 18-increment outline completed at 01:04 UTC. Fourteen questions became decisions; thirteen were answered by continuing accepted scope. A new idle-atmosphere direction generated another complete outline from 01:16 to 01:46 UTC. Two dependency lists changed and `RetainScopeAfterPlanningDirection` correctly rejected the replacement. The new protocol avoids generating that replacement. Producer planning recovery generation 3 permits ordinary discovery to reconsider the retained generation-2 block after installation; unchanged new failures remain parked.

The Phaser prerequisite ticket completed at 02:07 UTC and Sprint 1 became Completed. Story/Epic/Release delivery executions remained WaitingForChildren because scaffold tasks in the same Story were not yet scheduled. Producer `ReadProgressiveContinuationAsync` now distinguishes that boundary from aggregate acceptance. Exact completed prerequisite provenance, a completed sprint, no active sprint and unscheduled related work permit one revision-bound recovery. `AdvanceProgressivePlanningAsync` uses `ScheduledSprintSequence` to return the retained proposal for successor estimates/readiness. It does not refine a new Story until all current work and independent aggregate acceptance complete. The already subscribed `sprint.completed` event now runs attention reconciliation; reconnect discovery provides the same current-state check.

Director `BuildRunnerFollowThrough` and `EnsureToolchainDecisionAsync` identify runner approval as setup authorization and provide a business-bound `/settings/toolchains?businessId=...` link. `ToolchainSetup.SelectSetupBusiness` accepts only a business in the current accessible directory. Opening a link never starts certification. Existing administrator endpoints still require review of the exact provider, profile and Office image. Approval alone neither installs a plugin nor creates eligibility; published provider installation and successful certification remain necessary. Pause and custom answers do not imply setup permission.

Technical Director `MechanicalReviewChatClient` measures quoted SHA-512 literals in decoded review evidence, including escaped JSON, and supplies exact base64 length/decoded byte counts. A contradictory malformed-encoding finding gets one reassessment and then fails processing if still contradictory. It never converts a rejection to approval. The independent shared reviewer retains exact candidate/target binding and downstream QA. The observed eventemitter3 hash decoded to 64 bytes and matched public npm metadata; the earlier length rejection was incorrect. Planning guidance also requires standalone assertion commands rather than inline scripts or inspection-only commands and respects the assignment publication branch.

Regression entry points: Producer `PlanningFlowRecoveryTests`, `PlanningDirectionRecoveryTests` and existing `ProgressiveSprintHandoffTests`; Technical Director `PlanningFlowReviewTests`; Director `BuildRunnerFollowThroughTests`; platform `ToolchainSetupPageTests` and `PlanningDirectionJourneyTests`. The cross-agent journey executes real Producer request creation and Technical Director generation/replay against retained scope with one deterministic model response and no staffing/build/acceptance capabilities.

Rollout: publish/install Technical Director 6.0.3 before Producer 6.2.1, then Director 0.4.2. Deploy the matching C-Sweet UI for business-bound setup links. No schema migration or SDK/contract package release is required. Local verification and prepared bundles are not a live deployment, runner installation, certification or claim that Sprint 2 has started.

Verification: 502 Producer, 226 Technical Director, 328 Director, eight cross-agent workflow and 23 runner-setup tests passed (1,087 total; zero failures or skips). All three Linux framework-dependent published outputs passed self-test; `.csab` integrity and `.nupkg` versions match the manifests and release-note headings. Manifest authority declarations are unchanged. Local package metadata, checksums and test counts are retained at `.artifacts/cool-games-review/planning-flow-release-verification.json`.
