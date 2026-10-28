# Developer execution plans and progress recovery

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
and regression coverage in both repositories. Releases are Adrian **4.1.10** and
Daniel **2.1.9**. Apply runtime changes through published releases and the Installed
Agents update flow; local packages only verify the implementation.

Verified rollout on 2026-10-28 UTC:

- Both standalone package consumers passed **344 tests**, their self-tests and
  Linux bundle verification. Both `.nupkg` versions matched their manifests.
  Core parity passed for **65 files**.
- Adrian commit `f781dad74d18197ac0b223a038287d0313cafdc9` was published through
  [release build 40633646330](https://github.com/CrosswiredStudios/CSweet.Agent.Engineer.VideoGame/actions/runs/40633646330).
  Daniel commit `1f04521ea633eeb5fb4e6fae5d14c86d4a604aef` was published through
  [release build 40633653293](https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareDeveloper/actions/runs/40633653293).
  Both builds succeeded.
- The normal `IAgentImportPreviewService` / `IAgentDefinitionService.UpdateAsync`
  path verified the exact published prebuilt release and installed Adrian 4.1.9.
  Configuration revision 3 was acknowledged and the replacement runtime ran.
  Daniel has no installation in the local database; 2.1.8 is available to install.
- #54 recovered through the existing orchestration backoff after its old runtime
  lease expired. It resumed assignment revision 2 in workspace
  `0fb40f08-9e9d-4504-b27e-2f54ced09ce5`, with its saved source checkpoint and a
  new persisted execution plan recorded at 04:50:49 UTC. No ticket status, decision,
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

Validation: 350 tests in each agent, 52 focused platform tests, 66-file core parity,
package self-tests and verified NuGet/Linux bundles. Official release builds
[40642114353](https://github.com/CrosswiredStudios/CSweet.Agent.Engineer.VideoGame/actions/runs/40642114353)
and [40642122300](https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareDeveloper/actions/runs/40642122300)
succeeded. API and AgentHost were rebuilt and restarted individually through Aspire
to activate the platform changes. Adrian 4.1.10 acknowledged configuration revision
4; #54 was recovered using `IWorkBoardService.RetryItemAsync` with the current ticket
revision, retaining assignment revision 2 and its workspace. Daniel has no local
installation; the matching release is available.
