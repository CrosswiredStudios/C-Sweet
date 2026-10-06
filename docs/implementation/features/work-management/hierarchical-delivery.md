# Hierarchical delivery and release integration

This is the normative specification for new game and software installations. Published V1 assignments and historical workflow/profile revisions remain supported. New delivery uses `WorkExecutionAssignmentV2` and `work.execution.run.v2`.

## Completion and authority

Code flows task → story → shared release → repository default branch. Architects may bind an optional epic branch between story and release. Repository-specific `WorkDeliveryBranchBinding` records are authoritative, including workspace preparation, refresh, retries and PR targets.

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

V2 assignments carry principal, planning/scope revision, candidate and permitted outcomes. Aggregate reviews have no sprint identity. `ReadEvidenceAsync` returns exact documents, authorized immutable repository snapshots, child review outcomes and builds. QA binds actual command results to every candidate repository and every criterion. Artifact QA verifies identity, author, origin and content hash.

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

Normal Creative Director/Producer and Software PM/Architect kickoff and lightweight Producer kickoff use revised profiles and `HierarchicalProjectDelivery`. Engineer/Developer use story bases; QA validates code/artifacts and full regression; technical/manager reviewers inspect complete evidence; Build Release Engineer requests certified candidate builds. Copied specialist `VideoGameAgentKit` implementations were updated individually for V2 and exact evidence, preserving discipline-specific rules.

## Clean installation and checks

Select a **new database explicitly** and apply `HierarchicalDeliveryPlans` and `HierarchicalDeliveryFindings` through normal EF migrations. No running-execution migration, automatic wipe, live publication, deployment or public release is included.

Build/test/pack WorkManagement.Contracts 3.25.0 and Agent.SDK 3.59.0. Restore consumers from verified packages with both `UseLocalCSweetWorkManagementContracts=false` and `UseLocalCSweetAgentSdk=false`. Use `CSweet.Agent.Sdk`. `scripts/verify-hierarchical-agents.py` runs changed agents' package-consumer tests, self-tests and packing into the local validation feed. Import version-matched manifests and select the latest hierarchical profile revision for a new project.

`HierarchicalDeliveryServiceTests` exercises code/artifact aggregate lifecycles, independence, stale candidates, pause/cancel/idempotency and partial promotion. `HierarchicalDeliveryPostgresTests` creates only randomly named loopback databases for clean migrations, revision contention and outbox rollback; configure `CSWEET_COMPUTE_TEST_DATABASE`. `HierarchicalGitDeliveryTests` uses real Git for shared releases, optional epics, reviewed rework, stale targets, author bypasses and receipt replay. `GitHubAppClientTests` verifies unsupported guarantees produce blocks without unsafe writes.
