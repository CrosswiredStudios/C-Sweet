# Inferred owner capacity and recovery

`WorkPlanningJudgments.AssessAsync` in SDK 5.37.0 asks an accountable owner to assess effort and independently offer a bounded amount of work. A prior calendar allocation is not required. The model reasons from current commitments, capability, restrictions and uncertainty; it never receives a default allocation or a rule equating requested effort with capacity.

## Evidence and judgment

`WorkAvailabilityContext.ReadAsync` reads authorized work and calendar evidence for one employee and installation. Other employees' personal boards and calendar events are excluded. Proposed items are demand and are excluded from competing assignments. Related coordination remains visible as overhead without making its bookkeeping a new independent commitment. Cross-board discovery is bounded, and unreadable or unobserved coverage is explicit.

`WorkCapacityAssessment` separates three meanings:

- A positive `OfferedPoints` is an owner's proposal; management still chooses scope.
- Zero means the owner currently cannot offer work, with an explanation.
- Null means a material unknown prevents an offer, with focused `MissingEvidence`.

`State` is derived in code. Identity, keys, revisions, assessment basis and compatibility projections are also code-owned. The existing numeric estimate protocol remains compatible, with additive judgment and evidence metadata. `FromEvidence` treats a retained legacy zero as unknown rather than retroactively inventing availability.

`ForecastDrivenPlanning.Instructions` carries the same semantics across platform-brokered workflows. Neither a missing calendar allocation nor an idle runtime establishes zero capacity; an empty calendar also does not establish unlimited capacity. Genuine shortfalls require focused work owned by someone who can resolve them.

## Reuse and current-state fencing

`WorkPlanningJudgments.EstimateAsync` keys assessments by `AssessmentContract`, the request and a stable availability fingerprint. It reads current evidence before cache lookup and again before publishing. Legacy cached zero assessments remain historical evidence; the new contract reassesses them. Duplicate observations reuse the same judgment. A changed commitment requires fresh inference.

Producer `ReconcileEstimatesAndQaReadinessAsync` includes the current contract, availability basis and current capacity direction in specialist request identity. `SprintPreparation.CapacitySources` retains selected owners' evidence. `CapacitySourcesCurrentAsync` checks it before commitment and during readiness; changed evidence withdraws prepared capacity and returns the existing work to assessment. Normal staffing, preflight and sprint-start authority remain required.

Producer `ResolveCapacityShortfallAsync` uses shared `RequirementDecisions` inference to author a question and useful response options against the accepted source. It durably retains the draft and effect reference, attaches the native decision dependency to the existing task, and consumes current recorded direction through authorized follow-through. Questions and choices are not enumerated for games, operating systems or capacity categories. Native decision events wake dependent work; bounded review remains recovery discovery, not an execution grant.

## Regression and release evidence

SDK 5.37.0 passes 946 SDK tests, two sample tests, seven generated-template tests and the generated runtime self-test. `WorkAvailabilityContextTests` covers non-game owner scoping, duplicate reuse, legacy-zero recovery and calendar-change reassessment. `WorkPlanningJudgmentsTests` includes the actual Shardfall zero-capacity reply and preserves unknown, actual zero and partial offers independently of requested effort.

Producer 6.20.0 passes 593 tests and its runtime self-test. `CapacityPlanningTests` verifies inference-authored, deduplicated decisions for unknown and actual zero offers. `ProgressiveSprintHandoffTests` changes a calendar commitment after preparation, verifies withdrawal, and reaches sprint start only after a fresh assessment and preflight. Technical Director 6.12.0 passes 253 tests and its runtime self-test. Both consumers use packed SDK/contracts with sibling references disabled; package versions, source-release and dependency-lock audits pass. Work contracts remain 5.11.0.

Platform model, decision-wake and sprint-service checks pass 50 tests with four existing skips. These source checks establish behavior under controlled evidence; the [Shardfall run review](run-reviews/qfn-games-shardfall-2026-11-16.md) separately records the actual live outcome.
