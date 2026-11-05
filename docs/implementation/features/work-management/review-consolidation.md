# Consolidating redundant reviews

`WorkBoardService.ConsolidateReviewsAsync` implements an explicit human management action,
exposed by `WorkBoardEndpoints` at `POST /api/organizations/{organizationId}/work/boards/{boardId}/consolidate-reviews`.
It requires board read, sprint scope management and planning revision grants, optimistic ticket
revisions, an exact criterion-to-native-stage mapping, a reason and an idempotency key.

Only unstarted, unfinished review tickets from the active canonical sprint qualify. The
canonical ticket cannot depend on the reviews. Executing dependents and started aggregate
candidates block consolidation. The action cancels redundant review executions, retains their
history and criterion mapping, and redirects unfinished dependents to the canonical ticket.
Cancellation never grants acceptance; the canonical native quality stage still has to pass.

For an active delivery plan, the same transaction removes redundant child/predecessor/evidence
IDs and their digest/revision pins from unstarted aggregate declarations. Workflow planning
revisions propagate into parent workflow pins using `PropagateWorkflowPlanningRevisions`.
Equivalent dependency references change without invalidating the retained canonical code,
assignment revision or scope revision. Current source/criteria and branch bindings remain intact.

State, `reviews.consolidated`, item activities and the board notification outbox commit together.
A replay with different terms fails. A matching replay may repair the previous implementation's
single-revision parent pin propagation omission, only with unchanged workflow planning and
unstarted aggregates. That repair records `reviews.consolidated.references-reconciled`; it
cannot refresh arbitrary edited planning or waive completed/started delivery gates.

`WorkBoardServiceTests.ReviewConsolidation` covers nested references, stale revisions,
criterion mismatches, started work, missing native stages, dependency cycles, authorization,
idempotency and bounded repair. Live Prism Break consolidation retains #38 and cancels #39/#40
as duplicates of its existing technical review and independent Chromium QA.

`RepairConsolidatedReviewColumnsAsync` repairs historical cancelled reviews left in an active column. Prefer a configured Cancelled column, otherwise Done; preserve canonical Cancelled status. Matching receipt replay records `reviews.consolidated.columns-reconciled` and commits the board notification atomically. The All Work card footer displays canonical cancellation, and sprint badges identify completed sprints or workflow work outside sprints. An empty active-sprint view after Sprint 2 completion is expected while release workflow #43 is pending.
