# Bounded compute resume history

`WorkExecutionResumeHistory.Compact` projects compute waits into assignment inputs
for both `WorkOrchestrator.AssignmentOutcomes` and
`WorkDeliveryService.DispatchDeliveryAsync`. Each stage contributes only its current
`awaiting-compute` checkpoint. A later substantive outcome supersedes that wait.
Author, review, failure and rework outcomes retain their existing order and content.
Artifact QA still uses its exact current author selection.

This projection does not delete or rewrite `WorkExecutionAttempt.ResultJson` or
change authorization. Compute completion events remain wake hints; the resumed
agent reads its current environment, operation and retained evidence through the
existing SDK. The inbox's 256 KiB admission bound remains enforced.

PRISM BREAK task 62 exposed this issue after 331 callbacks accumulated enough wait
history to exceed inbox admission. The stage remained pending while each scheduler
pulse failed admission. Deploying this host projection permits the pending stage to
resume normally without a database repair or repeating completed browser checks.

`WorkExecutionResumeHistoryTests` exercises 1,000 callbacks with shuffled stored
attempts, current operation binding, retained substantive failures, independent
stages, superseded waits, bounded serialization and unchanged audit history.

`WorkOrchestrationService.LoadStageAsync` loads the current stage attempts through the item stage graph once. A redundant root include previously multiplied checkpoint histories during retries. The relational `WorkOrchestrationRetryTests.RelationalRetryLoadsLongCheckpointHistoryOnceAndPreservesEveryAttempt` covers 500 retained callbacks, a bounded join shape, unchanged attempt history and the normal durable retry event.
