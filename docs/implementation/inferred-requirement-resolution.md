# Inferred requirement resolution and retained evidence

Implemented after the [QFN Games / Shardfall investigation](run-reviews/qfn-games-shardfall-2026-11-16.md).
Substantive sufficiency, compatibility, options and next work are agent judgments. Identity,
authorization, source versions, command identity and accepted state remain platform-owned.

## Shared contracts and effects

Broker `WorkEstimateToolSchema` derives its envelope from `EstimateWorkItemRequest` and
`WorkEstimateProvenance`. Compatible wire additions no longer need a second handwritten property
list. Mechanical value/source checks remain in the broker; SDK `WorkPlanningJudgments` asks the
accountable author for effort, uncertainty, assumptions and capacity. It does not ask the model
to copy identities, revisions or tracking fields.

SDK `WorkContributionLinks` refers to evidence retained by the native collaboration service.
The source collaboration, current attributed artifact and destination identify an association.
The same contribution can support an Epic, a later Story or other work without becoming a new
contribution or reusing a command against another destination. Source retraction/inaccessibility
prevents use. A failed reference write creates separate SDK-owned repair work; it does not erase
the contribution or turn display publication into a new substantive approval gate.

SDK `RequirementDecisions.AssessAsync` asks inference to trace a requirement, distinguish actual
obligations from assumptions and author a focused question, choices, consequences and follow-through.
`Bind` supplies authoritative context and option identities, including free-text direction. The
existing native decision/card service owns presentation and authorized answers. No Windows-specific
option menu, required specialist or game-specific platform workflow is introduced.

`FollowThroughAsync` re-reads the current decision and uses inference with discovered granted tools.
It checks that decision again before every dispatch, computes command keys, and retains the result
for replay. Ordinary resource/work/delegation services still authorize their mutations. A chosen
direction does not establish capacity, authorize an installer, modify accepted scope or certify an
environment. Follow-through reports remain separate from verified readiness. Explicit owner holds
are preserved; the responsible evaluator must inspect actual changed evidence before proceeding.

`ToolchainReviewContext.Describe` separates certified build-host alternatives from player targets
and acceptance coverage. This matches the existing certification scheduler and execution runner,
which select an individually certified environment profile. A provider's supported-profile list
does not require provisioning every operating system. Inference still assesses compatibility and
genuine accepted verification obligations.

## Agent adoption and recovery

- Producer `AdaptiveSprintReview.ReadDesignAdviceAsync` retains attributed review facts and publishes
  destination-bound evidence references. Planning contract v7 permits current-state recovery.
- Technical Director `ReviewToolchainAsync` supplies normalized certified alternatives and asks for
  concrete incompatibilities, missing facts, requirement origins and independently useful work.
- Director `RequirementReview` reopens retained negative feasibility work once when its review
  contract, current technical participant installation, certified environment or answered direction
  changes. Unchanged events do not reopen the same review. Genuine remaining requirements use the
  shared inference-authored decision and normal authorized follow-through.
- `WorkRecoveryCapabilityHandler` includes the current broker/schema in the assessment basis, so
  changed code/contract evidence wakes the existing manager recovery task for fresh judgment.
  This remains evidence rather than an execution grant. `AgentTicketFeedback.FailureSentence`
  identifies validation/conflict operations and the required reconciliation instead of presenting
  those integration failures as an unexplained execution stop.

## Verification and release boundary

SDK **5.36.0**: 941 tests, two sample tests and seven generated-template tests pass. Producer
**6.19.0**: 590 tests. Director **0.14.1**: 453 tests. Technical Director **6.11.0**: 253 tests.
All three runtime self-tests pass. Agent consumers were checked against the final packed SDK
with sibling SDK/contracts references disabled; NuGet package versions and dependency metadata
are recorded in `artifacts/qfn-package-verification.json`. The contracts remain **5.11.0**.
The platform's affected checks pass 103 tests with three existing skips, including verification
against the packed SDK with sibling SDK/contracts references disabled. The local AppHost Debug
build also succeeds.

Regressions cover one contribution supporting multiple destinations, failed-projection repair,
retracted-source rejection, non-game requirement choices, current-decision fencing during tool
dispatch, duplicate follow-through, changed-evidence review continuation, retained answered direction,
and the actual Shardfall Windows-profile finding. These protocol tests do not prove live model quality.

SDK source: `f90cc2e8255c4df7ca837ac45e546eedae336f42`. Producer:
`23a46d989b545da60703fc9d7268ec94ba2842f7`. Director:
`aad24bca5ef1f3f8573e0af691a4331eff3ce8c9` (0.14.1, fresh specialist routing after a resumed negative review). Technical Director:
`b750db5591e98a48af751ee70d92188be6db5204`.

Use the published releases and Installed Agents update flow. Preserve the existing business,
documents, decisions, work and history. Do not patch approved package records, stored manifests,
digests or installed payloads. The run review is the authority for what live recovery was actually
observed; source tests and successful publication alone do not establish recovery.

The retained Shardfall run passed the three repaired failure paths after normal owner retries:
the original 8-point estimate saved, its Story gained a reference to retained advice, and the
existing feasibility collaboration completed with fresh positive evidence. The old Windows blocker
was superseded and the existing certified provider selected. Sprint 1 remains Planned with zero
reported capacity. Live decision-answer follow-through and automatic recovery without operator
intervention were not demonstrated; see the run review for timestamps and evidence identities.
