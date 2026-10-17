# Developer validation recovery

## Cause

After the dependency-pin task passed implementation, review, integration and QA,
`VGCD352DE8BC-14` stopped during specialist execution at 10:00:29 Pacific. The completion
report declared a quoted `grep -rcE 'cdnjs|unpkg|jsdelivr|ajax.googleapis' dist` search and
a `find` asset search. Both were refused by `WorkspaceValidation.IsStandalone`: it scanned
all characters without respecting quotes and its executable list excluded search tools.
The check therefore mistook literal regex alternation for a shell pipeline. The assigned
developer blocked immediately after verifying the report, without a chance to correct it.

Simply accepting grep would not establish an absence assertion: grep exits 1 when no
match exists, and find's successful exit only proves it listed files. Actual acceptance
assertions and exit statuses must remain authoritative.

## Mirrored correction

Adrian 4.1.1 and Daniel 2.1.1 share the correction in `WorkspaceValidation` and
`AssignedDevelopmentService`:

- `IsStandalone` recognizes quoted/escaped arguments and exact executable names. It admits
  read-only grep/find/rg searches, including literal regex alternation, while rejecting
  unquoted operators, substitution, unterminated quotes, status masking, redirection,
  find execution/mutation actions and rg preprocessors.
- Real shell receipts and source fingerprints still determine the result. A no-match grep
  exit is never silently converted to success. Validation guidance calls for assertion
  scripts that exit zero only when the expected behavior holds.
- `VerifyWithRepairAsync` allows at most two correction passes for assigned work, preserving
  source checkpoints before more model work and checking the corrected source again. Required
  checks, independent review and QA remain in force. Missing tools retain their existing
  decision path; cancellation, checkpoint failures and operational errors propagate.
- Personal plans use the corrected parser/guidance and retain their existing configured,
  durable plan repair budget instead of receiving another nested budget.

`WorkspaceResourceSizingTests` covers the incident commands, literal quoted operators,
genuine pipelines/substitution, unsafe search actions and real failed exit codes.
`HarnessExecutionTests` runs completion repair through file tools, retaining implementation
source, accepting only executed evidence, correcting a no-match search into an assertion
check, and stopping after two unsuccessful repairs. Existing personal-plan budget tests pass.

Each complete solution passed 294 tests without skips (588 total), both agent self-tests
passed, and `scripts/Test-DeveloperAgentCoreParity.ps1` passed for 53 files. Both NuGet
packages and self-contained Linux bundles were built, with matching manifest/package/note
versions and verified bundle digests:

- Adrian 4.1.1: `8cc7773f6d2fe33cebab45e177623cca52e9c721581a5d21698d25e5e96104c6`.
- Daniel 2.1.1: `9aa6bab41b91bf0693f45b51f5c37f9d80fc9a8bbe0362bf95a50e39e4b90b37`.

## Live recovery

Adrian's existing installation was upgraded through `AgentDefinitionService.UpdateAsync`
after checking its exact unshared definition, unchanged manifest authority and idle work.
The bundle is a local development build with a retained source archive, not a remote release.
Daniel's matching package is ready; no Daniel installation exists in this business.

`WorkOrchestrationService.RetryItemAsync` retried the existing `VGCD352DE8BC-14` stage with
the owner's application identity and current revision. Attempt 2 was queued at 10:30:19
Pacific. The active sprint, accepted scope, workspace and historical attempt are preserved.

## Verified live acceptance

The resumed attempt ran on Adrian 4.1.1 with configuration Current. It converted the
problematic completion checks into `scripts/verify-dist-clean.mjs`, retaining the
real CDN/image absence assertions instead of masking exit status. `npm ci`,
`npm run build`, `npm run verify:pin`, `npx tsc --noEmit`, distribution cleanliness,
dev-server and static-server checks passed on retained source.

- 10:38:29 Pacific: implementation completed with `code-published`.
- 10:47:14: Victor approved all 13 files across five diff pages (56,127 characters).
  The platform integrated the task into its Story as commit
  `358918aae5eeaf41f69977ed4fcb5f4d97ca8d6f`.
- 10:57:39: independent QA passed that exact integrated source and the ticket became
  Completed, with no current blocker. All six workflow stages are Completed.
- At 10:58:17, Production Sprint 1 remained Active; the next task,
  `VGCD352DE8BC-15`, had begun Running automatically.

QA initially submitted an extra unassigned branch criterion alongside a passing verdict.
The report validator refused that inconsistent report; the same QA attempt corrected
it to the three exact assigned criteria and completed without another blocked-stage
transition. Preserve this validation history; it was not bypassed.

QA's accepted evidence includes clean install/build, exact pin, CDN absence,
typecheck and real dev/static HTTP-serving tests. Browser WebGL rendering could not
be measured in its headless environment, and the snapshot omits `.git`; these remain
explicit environment notes, not claims of browser coverage or independent branch
inspection. Source identity and integration are platform-bound.

Read-only canonical verification, including stage summaries and the QA/integration
binding, is retained in `.artifacts/stall-diagnosis/developer-validation-accepted.txt`.
