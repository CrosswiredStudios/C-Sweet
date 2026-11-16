# Document evidence and owned rework recovery

`DocumentEvidence.ReadAsync` (SDK 5.38.0) resolves the authorized accepted package revisions and checks their SHA-256 contents. Both `DocumentWork.AuthorAsync` and `ArtifactDeliveryReview.ExecuteAsync` use the same source material. Domain adapters may provide a code-owned citation crosswalk; inference judges whether the actual source content supports each obligation. Historical numbering alone is not an acceptance gate.

`DocumentResearch.ToolsAsync` supplies only granted public search/page reads, with bounded observations and explicit limitations. It never supplies unrelated mutation tools. Models distinguish planning assurance from measured implementation results. Missing material evidence or authority produces a `DecisionRequired` blocked outcome through `DocumentWork.Blocked`; a calendar entry cannot substitute for a native decision.

`DocumentWork` retains attempt-bound drafts and revises the existing same-project, same-team, same-work-item artifact. Code binds publication identity, source citations and verified research extracts. `ArtifactDeliveryReview` requests only criterion-level judgment, binds criteria/identities in code, computes the verdict, allows one correction and checks current assignment/revision again before returning acceptance.

`WorkOrchestrationService.ReadCompletedOutcome` exposes the latest completed attempt independently of the current stage status, including a retained review whose stage is now blocked. Identity, outcome code and latest-attempt checks still apply. `RetryAsync` permits the current board manager or granted human to route a stalled negative review through its pinned author transition once. It preserves the review findings and independent QA; it does not accept a document or increase automatic retry limits.

Producer `ReviewAdaptivePlanAsync` includes active-sprint outcomes and delivered artifacts before historical advice. `StoppedWorkDependency` gives planning and workflow recovery the same material dependency identity. `WorkRequirementResolution` persists one inference-authored draft and decision receipt per source-bound dependency, then reads its current answer. `ResolveStoppedWorkAsync` immediately creates owned resolution work and attaches the decision to the recovery card. Decision events resume work; scheduled discovery remains a missed-wake fallback. Historical decision receipts remain audit evidence.

`AgentPlatformEventDispatcher.PendingHintGroups` coalesces a bounded batch of project-health hints by organization, project, recipient and event type. Older outbox records are acknowledged only after current-state delivery succeeds. Assignments, messages, decision events and owner-report requests remain individual deliveries. `WorkScopedToolInvoker` reuses only selected duplicate observations inside one callback, invalidating them on inference/effects. Exact item, source, grant and decision reads remain fresh. `AgentTicketFeedback.ReadContextAsync` supplies bounded recent material discussion with retrievable comment identities instead of replaying all progress receipts.

## Verification and rollout

SDK 5.38.0: 954 SDK tests, 2 sample tests and 7 package-only template tests; runtime template self-test. Producer 6.21.0: 594 tests. Technical Director 6.13.0: 253 tests. Both agents were verified with sibling SDK references disabled, packed as NuGet packages and checked as linux-x64 CSABs with published-output self-tests. The SDK, Producer and Technical Director release workflows succeeded. Platform: 239 affected tests pass, with one PostgreSQL-environment test skipped, and the local AppHost builds with zero errors.

Agent deployment uses published releases and Installed Agents updates. No approved package records, stored manifests or runtime payloads are patched. Live recovery evidence will be recorded in the QFN Games Sprint 1 run review.


## Local Office repair launch

`ExecutionFleetService.LocalSetupLaunchMethod` uses the configured, machine-matched Windows development launcher for both repair and upgrade. Previously repair selected the maintenance agent solely from its installed version, even when its connection was offline, so the UI could wait until the handoff expired without launching Windows recovery. Remote Offices retain certificate-authorized agent maintenance. Current owner authorization, repair preflight, one-use handoff, durable launch claim and administrator approval remain required. Regression coverage includes an offline local repair, the existing upgrade-readiness requirement and remote certificate checks (71 affected tests pass).
