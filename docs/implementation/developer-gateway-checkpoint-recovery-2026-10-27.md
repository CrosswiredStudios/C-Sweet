# Developer gateway and small-ticket recovery — October 27, 2026

## Incident evidence

Adrian 4.1.6 stopped `VGEDB0C76CAA-53` (`dependency-pin-phaser`) with
`The MCP service rejected the request: Bad Gateway`. The ticket was created at
13:57:43 Pacific; execution started at 14:09:29 and became Blocked at 14:38:18.
Its single execution used 18 provider calls totaling 1,662,224 milliseconds
(27 minutes 42 seconds), averaging 92.3 seconds. Command execution generally took
seconds. `AgentRunLogs.MeasurementKind = ProviderAttempt` avoids double-counting
queue transitions and streaming audit events.

The agent inspected registry metadata across several model turns, authored a
verification helper, corrected its ESM control flow and imports, and diagnosed
and removed UTF-8 BOMs emitted by framework file tools. Before the last model
request failed, `npm run check:phaser`, lockfile inspection and clean
`npm ci --no-audit --no-fund` had succeeded for Phaser 3.90.0. These results and
source survived in workspace `a1a46857-49c2-4a02-8dee-a3b21ec1c0f2`, checkpoint
`2026-10-27T21:38:13.875653Z`.

The final provider request was cancelled after approximately 35 seconds. The
recorded gateway error does not identify its upstream origin. Adrian's runtime
stayed Running until a later idle timeout. His generic exception handler returned
a successful work envelope with a Blocked disposition, preventing ordinary
transport retry.

## Mirrored agent changes

Adrian **4.1.8** and Daniel **2.1.7** share these changes:

- `DevelopmentEfficiency.Instructions` asks for minimal helpers, grouped relevant
  inspection and a short ordered install/check sequence, and discourages repeating
  dependency research or rewriting a working checkpoint on resumption.
- `WorkspaceProgress.ShellBatchTool` exposes `execute_workspace_commands` for up
  to eight standalone commands. `RunShellBatchAsync` uses the existing confined
  shell, records each executed command separately and checkpoints before starting
  another. A failed command or policy denial stops the batch. Invalid batches run
  no commands. Source fingerprints and actual exit codes still gate publication.
- `Utf8WorkspaceFileStore` delegates framework path/link validation and file
  operations while writing text without an implicit UTF-8 preamble. Explicit
  Unicode content remains intact. This also fixes shebang-prefixed scripts.
- `DevelopmentTransport.IsTransient` recognizes typed HTTP 502/503/504/408,
  interrupted streams and the SDK's exact legacy MCP gateway messages. It does
  not retry authority denial, invalid local data, cancellation or failed
  checkpoint envelopes as ordinary model recovery.
- `GameEngineerHarness.RunImplementationAsync` and Daniel's corresponding
  harness preserve edits and allow two fresh-session transport recoveries with
  bounded backoff. A source checkpoint failure stops model/tool continuation.
- `WorkspaceCheckpoint.SaveAsync` retries content-addressed gateway-failed
  uploads, without repeating the edit or command. Exhausted assigned transport
  failures return `AgentWorkResult.Failure(..., "runtime.transport", true)` so
  the existing durable inbox/orchestrator retry budget owns further recovery.

Configured token ceilings, accepted criteria, technical review and independent QA
are preserved. This reduces avoidable model turns; it does not change the selected
provider's inference speed or promise that persistent outages never exhaust retry.

## Verification and release

Each complete solution passed **331 tests**, zero failures or skips. Daniel was
also tested with both local SDK and WorkManagement Contracts references disabled;
Adrian's standalone project uses the published packages. Both agent self-tests
passed. `scripts/Test-DeveloperAgentCoreParity.ps1` passed for **62 files**.
Both NuGet packages were packed and their internal `.nuspec` versions verified.

`HarnessExecutionTests` covers real file-tool checkpoint survival across gateway
loss, legacy and typed gateway exceptions, authority refusal and bounded upload
retry. `WorkspaceProgressTests` covers separate batch receipts, checkpoint ordering,
first-failure termination, cancellation and refusal to run chained commands.
`AssignedCodingRuntimeTests` verifies a gateway failure returns retryable transport
failure rather than a Blocked outcome.

Official GitHub release workflows built and published the tagged source:

- Adrian: `54d40313113cc5bf4c0491fdb516832096b8d340`, tag `v4.1.8`.
- Daniel: `f4bcc1f185ea94fb92ce2cd7992fd4cce1625438`, tag `v2.1.7`.

## Live recovery

Browser navigation hung while the local API remained healthy. The recovery utility
in `.tmp/gateway-ticket-recovery` references the running API's compiled libraries
and invokes the same application services used by Installed Agents and the board:
`IAgentImportPreviewService.PreviewAsync` for the exact published release tag,
then `IAgentDefinitionService.UpdateAsync`, then
`IWorkOrchestrationService.RetryItemAsync` with the active business owner's existing
application identity and current ticket revision. It asserts exact commit/version,
prebuilt release provenance, unchanged manifest authority/schema, business scope,
idle affected work, the original gateway blocker and an existing checkpoint.

Adrian's installed package became 4.1.8, Available. Daniel has no installed
definition in this instance; his paired published release is available for normal
installation. The original ticket's specialist stage was retried with the stable
key `gateway-checkpoint-recovery-20261027-v1`; attempt 2 entered Pending at
15:09:54 Pacific. The workspace, assignment revision, sprint and historical attempt
were retained. No direct package/ticket database mutation, stored-manifest edit,
installed-runtime patch or fabricated completion was used.

At 15:11:10 Pacific the new runtime claimed attempt 2 with configuration Current.
The model inspected retained files, restored dependencies with `npm ci`, used
`execute_workspace_commands` for the focused checks and wrote a fresh outcome.
It did not rewrite the existing implementation. At 15:13:33 the specialist stage
completed with `code-published`: **2 minutes 23 seconds**, ten provider calls and
117.0 seconds of provider time. This measures checkpoint recovery, not a fresh
implementation benchmark, and provider latency varied between the two runs.

The published source commit is `868bd07b05c39427234da8005c939486a5cf46c4`.
Technical review approved it at 15:15:13. The platform integrated it as
`7d423a7968caf7b750a3525c19a8eb703e4b4ee9`, then started independent QA on that
candidate at 15:15:14. No review or quality stage was skipped.

The live run has not required gateway fault injection. Gateway recovery and its
authority/cancellation/checkpoint boundaries are verified by the regression suite.
At **15:19:38 Pacific**, independent QA passed the exact integrated candidate
with no blocking findings and `VGEDB0C76CAA-53` became **Completed**, with no
current blocker. Every stage (`ready`, `specialist-execution`, `technical-review`,
`task-integration`, `quality`, `done`) completed through normal orchestration.

QA initially added an unassigned branch-state criterion. The report validator
rejected it, and the same QA attempt resubmitted exactly the one assigned
acceptance criterion, keeping the branch observation in its summary. This
validation was preserved and not bypassed. The resumed specialist completed in
2:23; the complete retry-to-QA-acceptance interval was approximately 9:44,
including runtime recovery/startup and independent review/QA.
