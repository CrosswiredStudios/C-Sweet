# QA result persistence and completed-result activity

## Persistence

`SourceControlModelConfiguration` maps `SourceControlValidation.FailureMessage`
to PostgreSQL `text`, without a character limit. `UnboundedQualityFailureMessage`
upgrades the former `varchar(2048)` column without truncating retained summaries.
`WorkOrchestrator.RecordQualityValidationAsync` continues storing the complete
verdict output and failure summary; a review longer than 2,048 characters must not
roll back its stage transition, discussion and outbox records.

`QualityPublicationOutcomeTests` checks the 3,415-character failure observed on
ticket 23. `SourceControlModelTests` checks the unlimited mapping.
`QualityFailureMessagePostgresTests` upgrades a disposable database from the
previous migration and round-trips 3,415- and 16,384-character messages using the
upgraded column type. Its connection must name an `efficiency_validation` database;
it never resets the application database.

## Activity projection

`CurrentActivityService.ReadAsync` includes terminal inbox work only while its
exact authorized orchestration attempt is still pending/running. It retains the
matching finished attempt for the existing authorized progress feed. A returned
result is `Finalizing`, not an expired execution lease. After five minutes it is
`Needs attention`, with an explicit delayed workflow-result-processing message.
The projection omits the expired lease and uses result completion time as progress.
Historical completed deliveries are not rediscovered after reconciliation.

`CurrentActivityResultTests` checks the fresh and delayed result states, retained
feed identity, board-read authorization and exclusion of unrelated/completed work.
No completed QA callback is a claim that its product passed.

## Report identity and rejected-submission recovery

QA **5.2.1**, source `69a19ef4b7edc37015ecd8572ceb5e2e35e8f10f`, fixes
the next ticket 23 blocker. Browser compute finished successfully for the assigned
40-character commit, but the model submitted a 39-character transcription in
`sourceCommitSha`. `ValidateOutcome` rejected it with a combined identity/command
message; the model revised evidence prose instead of correcting the identity and
stopped after three rejected submissions. The final missing-file diagnostic hid
those actual rejections. No product verdict was accepted.

`GameQaReport.CreateTool` now removes source identity from the model-facing schema
and constructs `GameQaOutcome.SourceCommitSha` from the verified assignment in
code. `NormalizeArguments` accepts a matching legacy identity but explicitly
rejects a conflicting value rather than silently relabelling submitted evidence.
`GameQaExecution.ValidateOutcome` distinguishes identity, summary, command and
finding errors. `CorrectionBudget.LastRejection` survives an early model stop;
`GameQaReport.ReadAsync` receives that failure so the ordinary blocked outcome,
ticket and activity card retain the real rejected field and reason. Feedback states
remaining submission attempts, not an ambiguous correction counter.

The agent's 116 package-consumer tests, self-test and 5.2.1 package verification
pass; its GitHub release pipeline also succeeds. The SDK remains 5.2.0 and no new
grants are requested. Source-integrity rechecks, exact browser evidence, complete
criterion coverage and real validation results still gate acceptance. Install the
published release through Installed Agents before using the board's normal Retry;
never patch the retained result, package records or runtime payloads into a pass.

The accompanying Current Activity compute projection is platform-only and is
documented in `features/current-activity.md`. Its 162 focused regressions pass.
Deployment uses the Aspire resource controls plus terminal builds; C-Sweet does
not yet provide an in-app platform deployment action.

The published 5.2.1 release was updated through Installed Agents: the UI reports
Agent ready, with bundle download, verification and immutable sealing complete.
Rohan's existing installation selects that exact release commit. API, AgentHost,
WorkerHost and App were rebuilt and restarted; API and frontend checks returned
HTTP 200. No live QA callback was interrupted for deployment.

The board's ordinary Retry created attempt
`47807088-5341-422d-bea8-0f5db794ae56`, inbox work
`0bf644d7-b497-4cad-a52a-e6f0666a1c4c`, and runtime
`52d9520b-680d-4625-908a-79fcad14f9e6`. A real provider attempt started at
2026-10-22 01:17:21 UTC using QA 5.2.1. The execution is Running at quality with
no current block reason and the board shows ticket 23 in QA, Blocked 0. This is
verified recovery into fresh QA, not a passing verdict. Prior rejected outcomes
and the browser evidence remain retained.

## Ticket 23 criterion decision

The owner approved a prospective conventional-commit rule with an exception for
legacy platform-generated subjects. The exception is limited to existing ancestry
of candidate `4d3c2c392b2593080beaa485f7898edaf7114edf`. Existing history is not
rewritten. Source ancestry, browser checks and other product criteria remain.

Apply scope changes through `WorkDeliveryService.ControlAsync` and
`AmendTaskAsync`, not direct ticket JSON writes. The normal paused-plan, stopped
task, drained-attempt, current-revision and owner/manager authorization checks
remain. Amendment repins planning/delivery scope, retains previous publications
and verdicts, and creates a blocked author stage requiring an explicit retry.
The old failed QA report must not be edited into a pass.

`AmendTaskAsync` accepts planning entries up to 8,192 characters, matching task
description capacity. Ticket 23's existing accepted requirement is 4,308 characters;
the former 4,000-character amendment cap incorrectly prevented preserving it while
changing only the criterion. `TaskScopeAmendmentTests` covers intact preservation
of that length and rejection above the new bounded entry budget.

There is currently no dedicated human scope-amendment UI. Owner pause/resume is
supported by the service but the Delivery page shows those buttons only to its
assigned manager. A future UI should expose revision-fenced owner amendment,
the old/new criterion and its reason, plus result-processing failures rather than
requiring database/log diagnostics or an operator helper.

## Live recovery verification

The local database received `UnboundedQualityFailureMessage`; normal orchestration
then retained the complete 3,415-character failed validation for the assigned
candidate. The criterion amendment used the owner's existing authorization and
normal service transactions, with `ticket23-legacy-criterion-*` mutation receipts.
Planning and assignment revision are 3, delivery scope revision is 2, and the
delivery plan was restored to Active. No previous verdict was changed into a pass.

A normal board Retry reached the author stage and exposed a separate handoff
defect: `AssignedDevelopmentService.ReadReviewFeedback` in the installed Engineer
reads `candidateCommitSha`/`sourceCommitSha`, but the retained canonical QA
`WorkDeliveryReviewResult` uses `candidateDigest`. Its exact commit evidence is
present and matches; the parser rejects the missing legacy property before work
starts. The ticket is now genuinely Blocked at specialist execution, not falsely
Recovering in QA. Further retries alone cannot resolve this parser defect. A core
fix must cover both developer-agent repositories, their regression tests, package
versions, published releases and Installed Agents updates.

## Developer parser release recovery

The follow-up parser defect is fixed in Engineer **4.1.5**, source
`fa2ead0ec411a35b2f44d3fddd04de66c9c993b3`, and Software Developer **2.1.4**,
source `acc500888f63d94f22b2c49869221c4840e7ad46`.
`AssignedDevelopmentService.ReadReviewFeedback` accepts canonical
`candidateDigest` while retaining legacy identities only when all supplied values
agree. Exact commit evidence, bounded findings and rationale remain required.
No history-rewrite authority or automatic approval is added.

Both standalone package-consumer suites pass **311 tests**, both self-tests pass,
and their packed NuGet versions match their manifests and release notes. The
contributor parity script now includes `ReviewFeedbackTests` and passes **56 files**.
Both GitHub release pipelines produced verified linux-x64 bundles. The normal
Installed Agents update flow downloaded, verified and sealed each release; the
global definitions and Adrian's existing hire select those exact commits.

Ticket 23 was retried through the board's normal Retry action. Attempt
`e85500e2-2fb3-42f5-b784-95375bc13ed6`, inbox work
`aa34deef-8935-4d90-9ac4-ba513916be4a`, runs on fresh runtime
`82331ceb-f93d-4793-8208-a5c448b5443b`. At 20:50 UTC, an actual provider-attempt
record identifies agent version **4.1.5** and has a provider-start timestamp;
the repaired parser runs before that inference request. The board shows In
Progress with no blocked tickets. This verifies recovery past parsing, not a new
QA pass or task completion.
