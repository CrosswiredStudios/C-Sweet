# Engineering prerequisites and pending planning decisions

## Observed Neon Shatter stall

On October 23, 2026, Gemma Games' Neon Shatter project had seven published executable
items in Production Sprint 1, but the sprint remained Planned with no capacity,
specialist estimates, QA-readiness commitment, or execution record.

Victor 5.0.4 had created `dependency-pin-phaser`, an engineering task to choose an
exact version within accepted constraints, commit the pin and lockfile, and verify
the integrated result. Its technical proposal also retained an open Phaser-version
question. Producer 4.0.13 allowed only research spikes into estimates while any
authority question remained, preventing that prerequisite from producing evidence.

Owner selections on the incident's recommendation widget did not resolve the
separate project decision. Naomi's completed recommendation follow-up explicitly
reported that the technical recommendation was still awaited. Neither is proof
of a chosen engine version or completed engineering work.

## Correction and code map

- Technical Director `DependencyPinPlanning.PlanningProposalPayload` adds
  `engineeringPrerequisiteKeys` to the proposal payload, derived from its deterministic
  root-task binder. `CompletePlanningProposal` retains open questions and the original
  proposal/outline digest semantics. Director release: **5.0.5**.
- Producer `EngineeringPrerequisites.ReadEngineeringPrerequisites` validates declared
  root engineering tasks, capabilities, testable criteria, and dependent work. It also
  recognizes Director 5.0.4's deterministic `dependency-pin-phaser` key as a narrow
  compatibility path; it does not match English question or task-title keywords.
- `ProducerPlanningCycleState.EngineeringPrerequisiteKeys` records those keys with
  the current technical proposal digest. `CanPrepareWithPlanningDecisions` requires
  matching canonical provenance and engineering requirements. Ordinary implementation
  remains gated while authority questions are unresolved.
- `SpecialistAgent` uses that policy during initial estimation scheduling, attention
  recovery, candidate selection, and the readiness-to-finalization boundary. Estimation
  retains exact role-owned evidence, capacity, dependencies, independent QA, and normal
  platform preflight. Tentative nonselected draft items return to unscheduled work.
  Producer release: **4.0.14**.

The fix permits evidence-producing work; it does not answer project decisions,
invent dependency versions, waive accepted criteria, or expand grants. A completed
pin task is not itself authority to resolve unrelated scope or budget questions.

## Verification

- 438 Producer tests and 188 Technical Director tests pass with published package
  references. Both package identities and `.nupkg` versions were verified locally.
- `EngineeringPrerequisiteTests` covers old/new handoffs, stale provenance, absent
  engineering requirements, and invalid prerequisite declarations.
- `ProgressivePlanningTests.Accepted_increment_recovers_planning_and_starts_successor_once_across_restart_and_duplicate_wakes`
  now also runs with a pending Phaser question and a declared prerequisite through
  estimates, independent QA readiness, failed/passed preflight, and single start.
- `SprintDeliveryFinalizationTests` verifies that a new unresolved authority question
  blocks ordinary scope before finalization. The Director handoff test preserves
  the question and frozen proposal digest.
- The tag-triggered GitHub release workflows passed for Producer 4.0.14 and Director
  5.0.5, including Linux publication and bundle verification.

## Live recovery

At 09:57 Pacific, current-state reads confirmed Gabriel 4.0.14 and Victor 5.0.5 selected
and configuration-synchronized. Both official release tags were previewed through
`IAgentImportPreviewService`, checked for unchanged manifest authority/configuration,
and installed through `IAgentDefinitionService.UpdateAsync`. The default-branch update
check did not discover the tagged versions while their source PRs remained unmerged.
No approved package record, manifest, artifact digest or runtime payload was patched.

At 09:58 Pacific, the existing blocked planning task was requeued through
`IPersonalTodoService.RequeueAsync` with the current human owner's authority. Gabriel
reconciled the saved proposal and created `Collect estimates and QA sprint-readiness
evidence` (`c4f44ea6-5000-4347-b5ff-aa5b4d924df7`). Its recorded wait was
`Role estimation is pending: game-engineer (Active)`, with coordination session
`c7429be4-7644-4d0c-871c-039cd5f93cbe` actively requesting engineer estimates/capacity.
The original authority question remains pending and dependent implementation stays gated.
At 09:58:45 Pacific, the engineer estimate completed with three points, three points of
available capacity, and no blockers. At 09:59:16, QA returned `Ready: true`, no findings,
and only the exact Phaser-pin task in `ReadyWorkItemIds`. Gabriel completed estimation
reconciliation, returned decision-dependent draft work to unscheduled scope, and recorded
a one-task, three-point sprint. The normal preflight then passed, and Production Sprint 1
became **Active at 09:59:24 Pacific**, with an authoritative start timestamp and a completed
preflight commitment. This verifies live estimates, QA readiness, and prerequisite sprint
startup; it does not claim the dependency-pin implementation or integration is complete.

## Subsequent QA failure and prevention

At 10:08 Pacific the same pin ticket (`VG37155B016E-11`) failed at independent QA,
after engineering publication, Director review and task integration had succeeded.
The integrated candidate was `0b5cbeddf144e11e26d126ac5170bc1862e324c4`.
The retained QA receipt reported `Completed/passed` and contained five validations,
including `npm list phaser`, with **empty output in all five**. The repository and
commit bindings matched the candidate. `WorkDeliveryService.ValidateQualityEvidence`
correctly rejected the report; its combined diagnostic obscured the missing-output
cause. This is a report-contract failure, distinct from the original planning gate.

QA **5.4.1** addresses it before completion:

- `GameQaExecution.ValidateOutcome` requires a nonblank `DiagnosticExcerpt` on every
  command, for passing and failing reports. Summary/criterion prose is not a substitute.
- `GameQaReport.ReportFunction` returns actionable feedback before saving a report or
  invoking its durable checkpoint. Existing bounded corrections preserve genuine
  failures and never instruct the model to invent output.
- `DeliveryScopeEvidence.Bind` applies aggregate repository binding and approval
  coverage checks during submission, supplies exact candidate commits in code, and
  rejects ambiguous/unbound commands. A real failed check remains a failed check;
  rejection does not invent coverage of untested repositories.
- `SpecialistAgent.ExecuteDeliveryScopeAsync` applies the same bounded correction
  budget and first-valid-report completion policy as task QA. Source integrity and
  platform acceptance remain separate final checks.

Director **5.0.6** strengthens guidance and local validation:

- `PlanningAuthorityPolicy`, used by full and progressive planning, describes the
  missing fact, accountable role, affected work and closing evidence. Routine version
  selection is engineering work; actual engine/scope/budget/creative conflicts retain
  explicit management authority. The Director plans/reviews, engineering commits,
  and QA independently checks the integrated candidate.
- `BindDependencyPins` rejects a canonical prerequisite assigned to the Director,
  dependent on other work, missing execution capability, or missing testable criteria
  before it reaches the Producer. Existing decisions and accepted scope remain intact.

Verification: **156 QA tests and 192 Director tests pass**, including actual-harness
correction of absent output, correction before report/checkpoint persistence, exact
per-repository binding, incomplete coverage, preserved failures and malformed planning
prerequisites. Both self-tests and package checks pass. QA packaging retains the
existing NU5104 warning for its preview Shell dependency.

Both tagged release workflows succeeded and published Linux bundles with checksums:
QA run `39996047825`, Director run `39996059938`. Source reviews remain open in
the respective repositories' PR #1. The separate `extension-ci.yml` runs reported
failure; their cause has not been verified in this patch. Successful local tests and
tagged release verification do not imply every PR check is green.

The official tag previews and `IAgentDefinitionService.UpdateAsync` selected both
releases with unchanged manifest authority/configuration. Current-state reads then
confirmed QA 5.4.1 and Director 5.0.6 synchronized. QA was initially idle with
`PendingNextStart`; the selected package activated on the next normal work launch.
The existing human owner's application identity and `IWorkOrchestrationService.RetryItemAsync`
restarted only the stopped quality stage. Completed implementation and review were
preserved; no stored result, package payload or approval was manually replaced.

At **10:39:52 Pacific**, QA attempt `c15d4c23-5c60-4016-8f73-774d0e0954d7`
completed with `passed`. All five validations now carried nonblank captured output,
including the installed Phaser dependency tree, bound to the same exact integrated
candidate. The pin ticket and its one-task prerequisite sprint became **Completed**.
The separate Phaser planning decision remained **Pending** and the original planning
root remained **Blocked** on the follow-up read. This verifies the QA correction;
it does not claim that decision-dependent implementation has resumed.

## Recorded direction, leadership follow-through and successor planning

The owner's recommended-option selection recorded the actual Phaser decision at
**10:43:07 Pacific**, as `Decided/provide-direction`. Gabriel resumed from the
durable decision event. Subsequent failures were technical handoff errors; repeating
the owner selection was unnecessary.

Naomi **2.1.10** and SDK **5.3.1** implement completion checks:

- `PlanningFollowthrough.VerifiedPlanningEvidence` reads the current project board
  and sprint execution, verifies completed integration and independent QA, and binds
  criteria plus captured command output to the exact repository/candidate commit.
  `GeneratePlanningDirectionAsync` includes that evidence in its planning review.
- `QueuePlanningFollowthroughAsync` creates stable work linked to the exact project
  decision. `HandlePlanningFollowthroughAsync` rereads it before completion. Pending
  decisions, `request-more-evidence` and unanswered replacements retain ownership.
  New completed evidence can reopen an evidence request through governed supersession,
  with an evidence digest preventing unchanged repeated requests.
- Existing project work/decision subscriptions wake matching waiting follow-ups.
  Duplicate events do not repeatedly requeue ready work; unrelated boards cannot wake
  the commitment. Bounded future review recovers missed notifications.
- SDK `ManagerIncidentCoordinator.HandlePersonalCommitmentAsync` no longer converts
  every successful text response into completion. An unresolved current incident
  retains a waiting commitment. Original conversation, message and project bindings
  remain required, and closed historical incidents do not replay recovery effects.
- The one additional requested grant is **team-scoped `work.orchestration.read`**.
  It permits evidence inspection; existing project authority still governs decisions.

Director **5.0.7**, retained in SDK-aligned **5.0.8**, repairs coverage generation.
`ReadOrGenerateScopeOutlineAsync` supplies only the frozen outline and the current
source batch to coverage mapping, rather than competing global-ledger and earlier-phase
instructions. `ValidateScopeCoverage` names exact missing, unexpected, duplicate and
unknown keys. The live retry completed all four coverage pages without dropping any
of the 41 accepted sources.

Producer **4.0.15** addresses the resulting direction/scope identity mismatch.
`RetainScopeAfterPlanningDirection` requires the same accepted cycle, source ledger,
canonical identities, hierarchy and dependencies. It verifies the original frozen
digest and retained detail, preserving original criteria and completed work. The newer
specialist review answers authority questions separately from immutable execution scope.
`ProgressivePlanningProgress.ReviewedDirectionsDigest` is persisted with reconciled
questions, so a retry cannot skip that handoff or repeat it forever. Changed accepted
inputs and genuine scope amendments remain blocked for explicit reconciliation.

Verification: **558 SDK, 225 Creative Director, 193 Technical Director and 443 Producer
tests pass**. The SDK template journey also passes seven generated-agent tests. The
platform's published-SDK consumer check passes **266 tests**, with five opt-in PostgreSQL
tests skipped. All changed packages pack successfully with checked NuGet identities and
versions. Published dependency pins are used for agent verification. The central platform
SDK pin is also synchronized; its local build is verification, not a running-app deployment.

Source PRs remain open. The first two Naomi release attempts ran before NuGet exposed the
new SDK; after checking the public index, the official rerun passed. The separate bundled
extension provenance check reports a pre-existing `VideoGameAgentKit.cs` hash mismatch;
that failure is not represented as a passing PR check or repaired by altering an installed
package. Normal published-release preview/update services perform the live agent updates.

The final agent releases are Naomi **2.1.10**, Gabriel **4.0.15**, and Victor **5.0.8**,
with QA remaining **5.4.1**. Live reads confirmed all three updated leaders synchronized.
The normal owner-authorized requeue preserved the original planning commitment, the
owner's decided Phaser answer, all immutable accepted planning artifacts and the completed
pin ticket. At **11:23 Pacific**, Gabriel reconciled the retained work and created
`fb7dcb03-d70c-4ba6-975e-63330c3d0fa6` for new estimates and QA readiness. Independent
engineer, QA and Director estimation sessions started.

At **11:24:55 Pacific**, the estimation/readiness commitment completed with six
capacity-bounded executable leaves and **28 points**. Normal preflight commitment
`8ebc1829-2893-459d-8d96-97bcc1d86d51` passed, and **Production Sprint 2 became Active
at 11:24:57 Pacific**, with its authoritative `StartedAt` recorded. The planning root
retains an explicit wait for current increment execution and later scope continuation;
it no longer reports the Phaser decision or generator as a blocker. This confirms
decision-to-reconciliation-to-estimates/readiness-to-start follow-through. It does not
claim the new sprint's engineering and QA work is already complete.
