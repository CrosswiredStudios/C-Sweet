# Qwen Games / Prism Break — November 16 recovery

Organization `b91ec903-4a9a-46f9-953a-e7786c20fa25`; project `f7a52c6e-d378-4635-9237-44fbeaced5ba`.
This is the fresh November 16 business, separate from the November 15 business with the same names.
The [event ledger](qwen-games-prism-break-2026-11-16.json) records source IDs and observation limits.

## Observed stop

At the 7:26–7:30 p.m. PST observation, Office `4ba38d94-47c8-47c7-955d-83f32c1460b1`
was Ready with one connected node and nine workload slots. The board
`dc0d440b-e651-4f29-8c6f-48b114875753` contained eight Ready Epic containers,
zero canonical Stories and zero executable tasks. Production Sprint 1
`79b3f5c1-a1ac-4c2b-8577-2a02a2672b02` was Planned with no items.
The accepted brief and a provisional `roadmap-epic-1.story-1` technical draft were retained.
Planning had produced useful detail; admitting that detail onto the board was stopped.

1. `SpecialistAgent.ReviewAdaptivePlanAsync` rejected a review if `PlanningResourceContext.Fingerprint`
   changed during inference. `AdvanceProgressivePlanningAsync` classified the ordinary changed-facts
   race as a code defect and blocked the commitment. The fingerprint intentionally excludes heartbeat
   timestamps; the exact changed staffing/resource field was not captured and remains unknown.
2. `ApplyReviewEffectsAsync` mapped a consultation's capability hint directly to a role member.
   The actual question asked for Marcus's judgment, but `Producer` selected Gabriel himself.
   The platform correctly rejected the duplicate participant pair. Generic denial prose obscured
   the bad recipient binding as a permissions problem. Two dispatch failures occurred six seconds apart.
3. The incident report then failed with `project-health.stale_evidence`. SDK manager reporting already
   refreshed once, but a second evidence change still escaped as a failed callback.

Victor also had an `AgentModelJsonException` at 7:18:52 p.m. Its diagnostic ID is recorded in the ledger;
the precise rejected reply was not inspected, so its cause is not inferred here. Five scoped advice
commitments completed between 7:17 and 7:23 p.m., but did not admit the draft onto the board.

## Repair

SDK 5.39.0 `ConsultationRouting` filters supplied authorized participants, excludes self/unavailable
runtimes, and asks inference who can answer the actual question. Code owns recipient identities and
binds the selection. `ProjectIncidentReview.ReviewCurrentAsync` refreshes incident state and diagnostics
once; another race remains pending for durable discovery rather than failing unrelated work.

Producer 6.21.1 `ResolveConsultationAsync` retains the inferred recipient before dispatch.
`StartReviewConsultationAsync` returns a platform rejection to inference once, reads current participants
for that correction, and retains a second rejection with reporting-manager ownership. An accepted effect
is recorded only after the platform accepts it. Replays reuse the retained effect and tracked card.
`ReviewAdaptivePlanAsync` refreshes changed planning facts once; continued change waits while retaining
the pending Story. `AdvanceProgressivePlanningAsync` honors that wait before changing admission state.
The review contract and recovery generation advance so earlier cached judgments can be reassessed.

No game-specific exception, staffing grant, scope acceptance or synthetic sprint activation was added.

## Verification and rollout

- SDK: 959 tests plus two sample tests passed; the generated-template test passed seven tests.
  SDK package `CSweet.Agent.SDK.5.39.0.nupkg` metadata verified. GitHub CI and publication passed.
- Producer: 599 tests passed, including package-only SDK/work-contract verification with sibling
  references disabled. Published-output self-test and `.csab` verification passed; NuGet package
  `CSweet.Agent.Producer.VideoGame.6.21.1.nupkg` created. Published release workflow passed.
- The fixture `prism-self-consultation.json` retains the incident's bounded question and incorrect
  capability hint. Tests cover correct routing, durable replay, one platform correction, a second
  owned rejection, one planning-facts refresh and continued-change deferral.
- Source releases: [SDK 5.39.0](https://github.com/CrosswiredStudios/CSweet.Agent.Sdk/releases/tag/v5.39.0),
  [Producer 6.21.1](https://github.com/CrosswiredStudios/CSweet.Agent.Producer.VideoGame/releases/tag/v6.21.1).

Live update and active Sprint 1 verification are pending. Source tests and a published release are
not evidence of installation, Story publication or an active sprint.
