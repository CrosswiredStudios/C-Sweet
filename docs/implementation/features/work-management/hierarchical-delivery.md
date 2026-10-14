# Hierarchical delivery and release integration

## Exact artifact handoff compatibility

`WorkArtifactDelivery` in WorkManagement.Contracts 3.29.0 owns author output and
validated reads. SDK 3.65.1 `ArtifactTaskDelivery` and the 15 video-game
`VideoGameSpecialistAgentBase.CompletedOutcome` implementations emit its canonical
camelCase fields. `ArtifactDeliveryReview`, `WorkDeliveryService.TryDocument`,
`WorkOrchestrator.DependencyDocumentsAsync`, broker
`ArtifactCapabilityHandler.AssignedReviewRevisionAsync` and human manual review
use the same parser. Existing PascalCase persisted outputs remain readable without
a database rewrite; malformed identities and duplicate fields fail closed.
Digest, provenance, scoped assignment and independent criterion-level QA checks
remain authoritative. A malformed later author outcome cannot select an older
delivery to bypass current review.

Regression coverage: Contracts `WorkArtifactDeliveryTests`, SDK
`ArtifactTaskDeliveryHandoffTests` (creation, submission, persistence and QA),
Technical Director `ArtifactDeliveryHandoffTests` (actual recovered callback,
legacy persistence and tampering), platform `HierarchicalTaskRuntimeTests` and
`DeliveryReviewDocumentAccessTests`. `scripts/verify-artifact-delivery.py` verifies
all affected agents against packed dependencies, then runs self-tests and packs
their final manifest versions. Publish Contracts, SDK and agents in that order;
the running platform and installed agents need their normal update/restart before
retrying an existing blocked QA stage. No persisted ticket or outcome is rewritten
by this source change.

This is the normative specification for new game and software installations. Execution uses the single `WorkExecutionAssignment` and `work.execution.run` contract from SDK and WorkManagement.Contracts 4.0.0. There is no legacy execution adapter. See [canonical execution cutover](canonical-execution-contract.md) for package alignment and existing-work recovery. Historical workflow/profile revisions are separate from the execution envelope.

## Completion and authority

Code flows task → story → shared release → repository default branch. Architects may bind an optional epic branch between story and release. Repository-specific `WorkDeliveryBranchBinding` records are authoritative, including workspace preparation, refresh, retries and PR targets.

`GitWorkspaceCapabilityHandler.PrepareAsync` preserves the existing workspace on reconnect. For a new task assignment revision, `ResolveReworkBaseAsync` carries forward the immediately preceding revision's latest unintegrated publication for the same organization, repository, task and story target. Technical Review rework therefore edits the rejected candidate instead of rebuilding from an empty story. Completed integration receipts or merged publications select the current story instead, preserving other integrated work during QA fixes. Rework still has its own task branch/revision and must pass the existing review and integration gates. A previously prepared workspace is never silently reset to an older candidate; recovery of an already incorrect workspace requires stopping its writer and preserving its snapshot first.

| Scope | Required gates | Authority |
| --- | --- | --- |
| Code task | implementation, independent Technical Review, trusted story integration, QA of exact integrated commit | active sprint and activated plan |
| Artifact task | exact artifact revision, independent criterion-level QA | active sprint and activated plan |
| Story | completed scoped tasks, full candidate regression, trusted integration | activated delivery plan |
| Epic | completed stories, technical integration review, assigned Producer/manager acceptance; isolated epics also require candidate QA | activated delivery plan |
| Release | all accepted epics, candidate builds where assigned, full regression, technical readiness, explicit release-manager acceptance, promotion receipts for every repository | activated delivery plan |

Every task receives QA, including QA-authored work which requires another reviewer. Missing staffing blocks activation. Task QA is historical evidence for its exact revision; later story changes require current story regression. Deployment and public release are separate approvals. No mandatory CEO gate is introduced.

Tasks, stories and epics remain canonical board-local work. `WorkDeliveryPlan` aggregates project epics across boards/repositories. `PlatformWorkTypeCatalog` supplies `software-delivery.v2` / `project-delivery.v2` container Story types. Planning creates task→story→epic even for fixes. Administrative personal commitments are outside this workflow. Sprint snapshots, velocity and completion count executable tasks only.

## Scope, staffing and evidence

`WorkDeliveryService.ConfigureAsync` pins membership, planning revisions, criteria, assignments and topology. Managers approve scope; Configure-granted architects may amend topology but cannot approve changed membership/staffing/criteria. `ControlAsync` validates complete assignments, explicit participation, board grants, all participating teams' repository access and coherent branches before activation. Membership itself grants no access.

Amend a paused plan to create a new scope revision and supersede aggregate readiness. Cancelling required work cannot remove it silently. Story/epic integration allows later manager-approved remediation scope. Successful default-branch promotions make that release scope immutable; additional scope requires a follow-up release. `WorkDeliveryTaskAuthorization.RequireAsync` enforces approved task hierarchy/story target and staffing. Direct Done moves, transfers and legacy merge actions cannot bypass gates.

Canonical assignments carry principal, planning/scope revision, candidate and permitted outcomes. Aggregate reviews have no sprint identity. `ReadEvidenceAsync` returns exact documents, authorized immutable repository snapshots, child review outcomes and builds. QA binds actual command results to every candidate repository and every criterion. Artifact QA verifies identity, author, origin and content hash.

`WorkOrchestrationBoardState.SynchronizeTaskArtifactGrantsAsync` grants the assigned independent task QA reviewer temporary document read access after exact author delivery. Completion, rework, cancellation and scope amendments revoke that access. Aggregate reviewers require read access to their complete scoped board/repository set; story reviewers need only their story's scope. Human review forms bind exact revisions, criteria and actual test output.

`TaskIntegrationWorkActionExecutor` requires independent approval of exact source and target commits. QA receives the resulting story commit. Failures require a new reviewed fix branch from current story state; existing history is retained. Accepted QA documents become dependency inputs without a task-level Producer gate.

## Runtime and recovery

`WorkDeliveryService.PulseAsync` reuses stage/attempt persistence, inbox leases, admission, bounded attempts and concurrency limits. Plans span sprints. Pause prevents new aggregate dispatch/promotion. Cancellation cancels outstanding aggregate inbox work and revokes temporary artifact grants, preserving Git and evidence.

State transitions, agent/application event outboxes and audit outbox records are transactional. `WorkDeliveryCapabilities.Changed` wakes managers; events are hints, never authority. Consumers re-read current state. `ReadWorkDeliveryPlansRequest` supports bounded cursor discovery for reconnects. Domain keys and provider receipts prevent duplicate effects.

`WorkDeliveryFinding` retains deduplicated candidate-bound failures across scope amendments. Managers authorize remediation tasks through approved scope. `RecoverAsync` requires linked, completed, independently QA-verified tasks and resolution evidence, then fresh aggregate validation.

`WorkDeliveryPromotion` stores each repository receipt. Partial completion stays visible and resumes unfinished operations without reverting successful merges. Changes to source, target, documents, builds, scope or staffing invalidate readiness and require fresh applicable reviews/acceptance. Already promoted content changes require a follow-up release.

`InternalGitRepositoryStore.DeliveryBranchAsync` serializes trusted operations, creates immutable merge candidates, compares both refs, atomically promotes with a durable Git receipt and blocks author writes to managed refs. `receipt` recovers successful external operations after lost responses or rolled-back local state. The current GitHub adapter blocks managed delivery with an actionable reason because its configured provider cannot enforce orchestration-only branch writes and atomic comparison of both refs. It makes no unsafe ref update and preserves provider protections.

## Surfaces and agents

`WorkDeliveryEndpoints` exposes typed read/configure/control/evidence/review/accept/recover and human task finalization. SDK `PlatformWorkClient` provides agent operations without provider credentials or unrestricted merge authority.

Delivery's `ProjectReleases` shows manager, scope, topology, reviewed/tested revisions, builds, criteria, blockers, findings and repository receipts. Human configuration/acceptance use revision checks. New task workflows have no standalone Merge or merge-decision column. Producer Review / Manager Review are epic gates; sprint cards show parent progress without including parents in totals.

Normal Creative Director/Producer and Software PM/Architect kickoff and lightweight Producer kickoff use revised profiles and `HierarchicalProjectDelivery`. Engineer/Developer use story bases; QA validates code/artifacts and full regression; technical/manager reviewers inspect complete evidence; Build Release Engineer requests certified candidate builds. Copied specialist `VideoGameAgentKit` implementations were updated individually for the canonical execution contract and exact evidence, preserving discipline-specific rules.

## Clean installation and checks

SDK 3.63.0 `DeliveryBuildReadiness` permits Release build-readiness without a separate hire.
Hierarchical staffing prefers a Build Release Engineer, then a Technical Director or Developer
advertising explicit stage support and canonical execution. The shared handler requests configured
certified builds for every exact candidate repository and returns durable pending identities.
Source authors may request these builds; independent product QA and technical review remain
required. Producer 2.19.5, Technical Director 2.14.4, Software Developer 1.15.2 and Game Engineer
3.2.3 pin this SDK. Publish SDK first, then release and update the agents through the normal
owner-operated process. No deployment or public release permission is added.

Select a **new database explicitly** and apply `HierarchicalDeliveryPlans` and `HierarchicalDeliveryFindings` through normal EF migrations. No running-execution migration, automatic wipe, live publication, deployment or public release is included.

Build/test/pack WorkManagement.Contracts 3.25.0 and Agent.SDK 3.59.0. Restore consumers from verified packages with both `UseLocalCSweetWorkManagementContracts=false` and `UseLocalCSweetAgentSdk=false`. Use `CSweet.Agent.Sdk`. `scripts/verify-hierarchical-agents.py` runs changed agents' package-consumer tests, self-tests and packing into the local validation feed. Import version-matched manifests and select the latest hierarchical profile revision for a new project.

`HierarchicalDeliveryServiceTests` exercises code/artifact aggregate lifecycles, independence, stale candidates, pause/cancel/idempotency and partial promotion. `HierarchicalDeliveryPostgresTests` creates only randomly named loopback databases for clean migrations, revision contention and outbox rollback; configure `CSWEET_COMPUTE_TEST_DATABASE`. `HierarchicalGitDeliveryTests` uses real Git for shared releases, optional epics, reviewed rework, stale targets, author bypasses and receipt replay. `GitHubAppClientTests` verifies unsupported guarantees produce blocks without unsafe writes.

## Team compute and review URLs

HierarchicalProjectDelivery.PrepareAsync selects an eligible Technical Director advertising work.execution.compute-test.v1 and pins ReadinessMode = compute-test in the release branch. Certified recipes are optional for sprint activation on this path. DeliveryComputeTest discovers project defaults and source-compatible execution options, transfers the exact candidate, builds/runs it in owned compute, and publishes a health-checked local review URL. WorkDeliveryService.Compute verifies the authoritative archive, exact command and current provider receipts before advancing to independent QA. awaiting-compute persists pending operations and bounded recovery deadlines without spending the failure budget. Technical Director TeamCompute discovers certified alternatives, retains project compute options and sends current test URLs to the project manager and identified CEO. ProjectReleases exposes those links with reachability and expiry. No automatic network grant or public hosting is introduced.

Publish WorkManagement.Contracts 3.26.0 then Agent.SDK 3.64.0; deploy C-Sweet and update Producer 2.19.6, Technical Director 2.15.0 and Software Developer 1.15.3 through the owner-operated release process. Live Hyper-V and game acceptance remain owner testing; code verification does not provision live resources.

## QA report draft staffing

ConfigureWorkDeliveryPlanRequest.TaskAssignments carries proposed author and quality-review assignments while a draft plan is being created. WorkDeliveryService.ValidateAssignmentsAsync recognizes QA-owned Artifact reports using accepted planning when delivery metadata is not yet populated and checks the proposed review against the current board manager. Proposals do not authorize execution: ValidateCurrentScopeAsync and ValidateTaskDeliveryAsync require finalized delivery and persisted independent review assignments before activation and on subsequent reconciliation.

HierarchicalProjectDelivery.PrepareAsync submits the proposals, finalizes task delivery against the returned draft ID, then activates. DeliveryConflictReason preserves task-specific configure/finalize/control conflicts as DeliveryStaffingException so Producer workflow and preflight commitments record recoverable waits. Product author self-review remains prohibited. Producer QueueWorkflowRecoveryAsync includes the QA proposal policy generation in its wake fingerprint; the explicit Retry staffing command requeues both workflow recovery and sprint-readiness commitments without resetting completed work or attempt history.

Publish WorkManagement.Contracts 3.28.0 and Agent.SDK 3.64.1, then update C-Sweet and Producer 2.19.7. The Technical Director and Developer versions from the compute-test release do not need another change for this correction.
