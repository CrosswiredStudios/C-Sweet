# Developer execution plans and progress recovery

## Update-source repair (4.1.14 / 2.1.13)

The November 5 developer source reconciliation restores the complete execution core
from `codex/sprint-flow-efficiency` and combines it with the MCP/browser changes below.
The default branches had committed stash-conflict markers and omitted supporting
execution files. `AgentImportPreviewService.PreviewAsync` reads that default-branch
manifest; invalid JSON prevents update discovery before any installation occurs.
Publish the repaired GameEngineer 4.1.14 and Software Developer 2.1.13 source, including
the restored files and matching notes, before fetching updates. Both standalone
package-consumer suites pass 371 tests, shared-core parity covers 70 files, and both
Linux bundles pass self-test and package verification. Local repair does not change
GitHub or any installed agent.

## MCP service recovery and real browser evidence (4.1.13 / 2.1.12)

`DevelopmentTransport.IsTransient` recognizes the exact SDK MCP HTTP-500 failure,
including its legacy exception representation, alongside the existing gateway
failures. `GameEngineerHarness.RunImplementationAsync` and its Daniel counterpart
preserve source and bound in-process recovery; `AssignedDevelopmentService.ExecuteAsync`
returns a retryable `runtime.transport` failure after that allowance. A runtime
callback finishing successfully is not evidence that its ticket completed. Unrelated
HTTP 500s, capability denials, invalid data and cancellation retain their existing
failure boundaries. No model setting or generation budget is reduced.

`BrowserSmokeCheck.Session.Tool` exposes `check_browser_build` in assigned and
personal development harnesses. It restores a trusted, dependency-free Node/CDP
helper, uses prepared Chromium, serves only the specified static directory on
loopback, and records actual startup exceptions, failed assets, visible surface
and a PNG screenshot. It downloads no browser or project package and adds no
application instrumentation. `test-results/browser-smoke` survives source
checkpoint transfer; it belongs in the repository's ignore rules rather than
authored product files. Screenshot output does not itself prove visual quality,
gameplay or independent QA.

For browser-visible assigned acceptance criteria, `BrowserSmokeCheck.Required`
enables `Session.RequireEvidence` inside `WorkspaceValidation.VerifyWithRepairAsync`.
A model-authored passing report or a static-only check cannot replace a successful
browser-tool receipt. A changed source fingerprint or successful dependency restore
(`WorkspaceValidation.InputRevision`) invalidates that receipt. Generated test output
does not invalidate source. The same bounded report-correction path requests the
missing browser check before publication. Non-browser tasks and pure dependency-pin
work do not acquire a browser requirement.

`AssignedDevelopmentService.BuildAssignmentPrompt` serializes a description once
when the planner also copied it into requirements, and retains each distinct
additional requirement. Role profiles use the single durable execution plan and
no longer contradict infrastructure handoff with a promise of skipped validation
in another environment.

Regression coverage: `HarnessExecutionTests`, `AssignedCodingRuntimeTests`,
`BrowserSmokeCheckTests`, and `DevelopmentEfficiencyTests`. Real headless-Chromium
verification in the C-Sweet review workspace tested a valid canvas, startup
exception, missing asset, blank page and the exact retained Prism Break scaffold.
The latter reproduced `this.scene.getKeys is not a function`; the screenshot showed
a blank playfield. This is retained test-copy evidence, not a change to live game
source or a claim of project recovery. Both developer releases must pass
`scripts/Test-DeveloperAgentCoreParity.ps1` and their standalone package-consumer
tests, self-tests and Linux bundle verification.

Adrian and Daniel share `DevelopmentWorkflow`, connected by
`AssignedDevelopmentService.ExecuteAssignedTicketAsync` and
`PersonalDevelopmentService.AdvanceAsync` (the deploy partial).
The workflow uses the existing Microsoft Agent Framework harness and platform
operating-state grants. No new provider, external permission or model setting is required.

## Execution

- `record_execution_plan` records three to five `ExecutionStep` entries, each linking
  an accepted requirement to its verification. Repeated calls reuse the existing plan.
  Initial file inspection is allowed before planning; source edits and commands
  require a plan. There is no separate planning-agent conversation.
- Progress checks run automatically after eight workspace operations. The optional
  `review_execution_progress` tool updates the current step and next action; it
  never gates the next operation. Parallel batches can cross a review boundary.
  Source fingerprints, novel command/check results and new relevant file inspection
  establish progress. Repeated unchanged inspections/checks and net-zero rewrites do
  not. A failed check can provide new diagnostic evidence.
- Real source progress can continue even when it consists of repeated edits to one
  file. Those edits prompt a focused validation check. Two windows without new
  evidence checkpoint source and resume in a fresh model session, at most twice in
  the same callback before yielding a retryable `DevelopmentProgressException`.
  Novelty evidence survives those fresh sessions. There is no five-minute ticket timeout or reduced
  configured model budget. Existing command shutdown safety remains separate.
- `PlannedVerification.RunAsync` executes existing plan-named npm checks before the
  first model turn on recovery, and after two automatic progress windows. It runs
  at most two checks per intervention, checkpoints their actual results and stops
  at the first failure. It accepts literal verification script names already in
  `package.json`; it never invents deployment commands or another test framework.
  Fresh receipts suppress duplicate checks until source or dependencies change.
- `WorkspaceValidation.Record` invalidates earlier receipts after a successful
  dependency restore. Runtime directories are excluded from source fingerprints;
  restoring `node_modules` must therefore explicitly invalidate a pre-restore
  missing-module failure as well as a pre-restore pass.
- Todo status and model-written outcomes never replace `WorkspaceValidation`
  receipts or publication, technical-review and QA gates.

## Preservation and infrastructure recovery

`DevelopmentWorkflow.LoadAsync` scopes revisioned `ExecutionPlan` operating state
to workspace and task/assignment revisions. The SDK excludes `.csweet` from
source archives, so storing the plan only in that directory would lose it during
workspace replacement. Plans and resume summaries are context, never authority.
The current assignment is always reread before execution.

`report_infrastructure_blocker` references an actually executed command and retains
the resource, recovery action, next step and source fingerprint. Instructions allow
one bounded runtime probe and one approved setup attempt. `GameEngineerHarness`
and `SoftwareDeveloperHarness.RunImplementationAsync` stop inference, upload source
using the still-authorized callback token, and return control. A revoked parent
token cannot authorize another upload. Checkpoint failure prevents continuation.

Assigned stages return retryable `development.infrastructure` or
`development.progress` failures through the existing durable inbox/orchestrator
retry policy. Personal tasks use their normal scheduled recovery callback without
consuming code-repair attempts. This is bounded recovery, not an extra polling loop
or a promise that missing resources can be provisioned without current grants.
Persistent outages can still exhaust the platform retry policy.

On resume, the retained plan identifies unfinished work and the missing resource.
The recorded infrastructure command must execute successfully before the retained
gap is cleared. A completion report written while that gap remains cannot be
accepted. Passing a runtime probe still does not replace remaining acceptance
checks, and skipped browser checks must not be reported as a successful browser run.

## Verification and releases

`DevelopmentWorkflowTests` covers plan gating, continued productive windows,
single-file progress, repeated inspections/rewrites/commands, early-review bypass,
infrastructure handoff and fresh-workspace recovery, operating-state persistence,
checkpoint failures and completion attempts with an unresolved infrastructure gap.
Existing personal-plan recovery fixtures exercise the additional operating-state
record and plan tool without removing their publication/recovery assertions.

`scripts/Test-DeveloperAgentCoreParity.ps1` compares the shared controller, source
and regression coverage in both repositories. Releases are Adrian **4.1.12** and
Daniel **2.1.11**. Apply runtime changes through published releases and the Installed
Agents update flow; local packages only verify the implementation.

Verified rollout on 2026-10-28 UTC:

- Both standalone package consumers passed **357 tests**, their self-tests and
  Linux bundle verification. The `.nupkg` metadata versions were inspected and
  matched the final manifests. Core parity passed for **68 files**.
- Adrian commit `7eae55e` was published through
  [release build 40647639637](https://github.com/CrosswiredStudios/CSweet.Agent.Engineer.VideoGame/actions/runs/40647639637).
  Daniel commit `81db216` was published through
  [release build 40647648824](https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareDeveloper/actions/runs/40647648824).
  Both builds succeeded.
- The normal `IAgentImportPreviewService` / `IAgentDefinitionService.UpdateAsync`
  path verified the exact published prebuilt release and installed Adrian 4.1.12.
  Configuration revision 6 was acknowledged. Daniel has no installation in the
  local database; 2.1.11 is available to install.
- #54 retains assignment revision 2 and workspace
  `0fb40f08-9e9d-4504-b27e-2f54ced09ce5`. Its checkpoint was confirmed at
  07:08:45 UTC before the final update. `IWorkBoardService.RetryItemAsync` recovered
  it after the prior runtime lease expired, using the current ticket revision.
  Recovery is not evidence of ticket completion. No ticket status, decision,
  approved package record, artifact digest or runtime payload was manually patched.

## Correction after #54 reblocked

The 4.1.9 / 2.1.8 controller rollout verified plan creation, not a completed ticket.
#54 subsequently hit two defects: pre-plan warnings were still counted after the
plan was created, and the mandatory eight-operation gate rejected legitimate
inspection calls, including parallel calls from one response. The retryable
`development.progress` error was then converted to `task.repeated_issue` by
`AgentTicketFeedback.RecordFailureAsync`. Its unrecognized code produced a generic
"execution stopped unexpectedly" comment and manager escalation.

4.1.10 / 2.1.9 replace that mandatory gate with automatic observation, allow initial
inspection, count genuinely new source context as progress and recover true stalls
inside the existing callback before spending platform retries. Regression tests
include the pre-plan warning sequence, parallel batches crossing boundaries and
fresh-session recovery without replaying source edits.

`PreparedBrowserRuntime` discovers the read-only browser cache installed by
`CSweet.Office/build/provision-node-toolchain.sh`. It preserves the prepared
`CHROMIUM_PATH`, `CHROME_BIN` and `PLAYWRIGHT_BROWSERS_PATH` values in the otherwise
clean shell and tells the model the discovered executable. Browser availability is
still checked in the actual guest; source inspection never replaces runtime proof.

`AgentWorkFailure` and `AgentTicketFeedback` recognize both development checkpoint
codes. The platform respects its existing bounded retry budget instead of declaring
a repeated ticket issue while recovery is available. Exhausted recovery remains
visible with an operational diagnostic; no acceptance criterion or grant is waived.

The platform changes passed 52 focused tests. API and AgentHost were rebuilt and
restarted individually through Aspire to activate them. Later live evidence found
that a recovered model could still spend many turns inspecting an inherited
browser helper before running it. 4.1.11 / 2.1.10 added `PlannedVerification`.
Its first live recovery executed the planned check at 06:54:48 UTC, before the
first model call at 06:54:49. That check exposed missing Phaser dependencies;
the model restored them. 4.1.12 / 2.1.11 correct stale validation receipts after
that restoration, with regression coverage for both stale passes and failures.
Browser and technical-review acceptance remain required; a resumed callback or
successful dependency installation does not prove #54 complete.
