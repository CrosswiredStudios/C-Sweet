# Progressive game planning

## Entry points and scope

Technical Director `SpecialistAgent.GenerateProgressivePlanningAsync` in the sibling
`CSweet.Agent.TechnicalDirector.VideoGame` repository implements an explicit negotiated
`progressivePlanning` extension to the existing planning-cycle artifact. Producer
`EnsurePlanningSessionAsync` enables it for the lightweight team path without a dedicated
Designer or active role repair. Other callers retain their existing planning path.

`AcceptedScopeSources` maps every paragraph of every exact accepted package revision to a
scope source identity. `ValidateProgressiveOutline` requires a complete Epic/Story outline,
one bounded Story per increment Epic, complete source coverage and a valid dependency graph.
The scope key binds the workstream, team, board, profile, approved package identity/version/
digest, planning fingerprint and authenticated Producer directions. Roster revisions do not
rewrite approved scope. Scope coverage retains requirements and non-goals; it is never
evidence that they have been implemented or accepted.

The first response contains the entire scope outline and no executable leaves. Producer
`AdvanceProgressivePlanningAsync` publishes those containers through the existing canonical
backlog publisher, then starts one durable, idempotent, dependency-ready Story refinement.
`ValidateProgressiveProposal` rejects changed containers, lost prior detail, unrequested
future leaves and weakened original criteria.

`ValidateStoryRefinement` limits the requested Story to twelve executable tasks, maps its
description and every original criterion to task owners, retains each criterion verbatim,
and rejects dependencies on future unrefined containers. Implementation descriptions are
generated three tasks per page. The immutable outline, task index, brief pages and small
completion marker use separate revision-checked operating-state records. Restarts reconstruct
validated pages rather than replaying a large response or overwriting scope.

## Delivery and continuation

SDK `HierarchicalProjectDelivery.PrepareIncrementAsync` explicitly selects fully detailed
Epic increments. `StaffWorkflowAsync` excludes already accepted Epics and leaves future
containers outside the current delivery plan. This retains the same staffing, estimates,
QA readiness, task/Story/Epic/release independent acceptance, integration, build readiness
and authoritative preflight as the complete-project helper. Empty, foreign, undetailed or
conflicting scope selections fail before activation; existing active/draft scope is preserved.

`HasAcceptedProgressiveIncrements` requires completed delivery plans covering the published
increment Epics as well as terminal canonical tasks. No delivery plan, an active plan, or
task completion without aggregate acceptance cannot advance the next Story or complete the
planning commitment. Durable coordination completion wakes the owning commitment through
`WorkItemMutationEngine.WakeCoordinationWaitsAsync`; bounded reconnect discovery handles
missed notifications. Events remain wake hints and every reconciliation re-reads current state.
No new capability grants, public contract changes or database migrations are required.

## Verification and local rollout, 2026-10-16

- Technical Director 4.1.0: 148 tests passed, including the three complete original incident
  response captures, outline-before-brief publication, source coverage, frozen criteria,
  future-dependency rejection and checkpoint restart/replay. Self-test passed.
- Producer 3.2.0: 369 tests passed against the actual SDK 4.5.0 NuGet package with local SDK
  references disabled. Tests publish the complete outline before requesting detail, retain
  future containers, reconcile one increment, and refuse continuation without completed
  aggregate delivery acceptance. Self-test passed.
- SDK 4.5.0: 440 SDK tests and two sample tests passed. The standalone template verification
  generated a temporary repository using packaged dependencies; its seven tests and self-test
  passed. The platform maintenance consumer also built with local SDK references disabled.
- All three packages were packed and their `.nuspec` versions verified against filenames.
  Agent versions, manifests, implementation identities and release-note headings agree.
- Only Sweet Sweet Games' existing Victor and Gabriel installations were upgraded through
  `AgentDefinitionService.UpdateAsync`. Full manifest equality except version was checked.
  Exact source snapshots and explicitly local-development runtime artifacts are retained in
  ignored `.artifacts/progressive-planning`; no remote publication was claimed.
- Installation recovery initially reopened the old blocked monolithic request. Cancellation
  on a Blocked session is a service no-op, so the reopened Active attempt was then cancelled
  through `AgentCoordinationService.CancelAsync`. Its transcript and historical packages remain.
  An explicit resume raced the installation recovery; the authoritative commitment was already
  Ready and was subsequently claimed, rather than issuing a second manual retry.
- The same project and planning commitment resumed. New coordination session
  `df639bfa-9b63-4e3e-b3e3-5792d42b20b2` carries `progressivePlanning.phase=Outline`.
  At 15:05:37 Pacific, Victor 4.1.0 started real model run
  `e98095fe-749e-4f8e-ab7e-305a4b7fa602`, bound to leased work
  `29717ce4-fde7-438a-844c-269669ac8984`. The retained source digests are
  `69baa5b0192c00f6cf7daa005c33c541d23ddb32f6538edc8bcfd755d6149f3e` (Victor) and
  `3b48249457fde868c09293475c226e865f7143ee9b906b97d96479972a0708fe` (Gabriel).
  This verifies resumed outline generation; it does not claim published tickets or a playable
  build before those separate authoritative outcomes occur.

Approved documents, package acceptance, agreed scope, project/team identities and completed
work were preserved. The earlier escalation fixes and unrelated hiring edits remain intact.
