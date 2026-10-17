# Prism Break runtime build interruption

Ticket `VGAEBFF008A8-33` (`88e4c110-83f0-455c-82cb-fd164b0cfbbb`) stopped during
implementation on October 16, 2026. Three Adrian 4.0.7 attempts started
`npm run build`, stopped returning command progress and work heartbeats, and lost
their authenticated MCP sessions. The third failure exhausted the stage's three
attempts. No candidate publication, technical review, task integration or
implementation QA occurred.

The failure was reproduced in the exact guest image: the 1 GiB VM exhausted its
available memory during the build and lost the authenticated broker channel.
The same image, source and command sequence completed at 2 GiB. This identifies
an insufficient guest memory envelope as a concrete cause of this failure path;
the original attempts have no kernel record identifying a particular killed
process. Verified: October 16, 2026.

## Evidence and reproduction

The retained source archive has SHA-256
`1b244708b0caa079eee4ac870b5cb87730e9c1e686b93805489e312f0973cbef`.
Local evidence is retained under `artifacts/ticket-vgaebff008a8-33/`, including
`investigation.json`, the original archive and its extracted source.

All three original workloads used one CPU, 1,024 MiB memory, guest digest
`sha256:385eb95e0d2ec4be6ea03107aa082416d42c63dcfa6ba8b275f9021e99128be4`
and agent artifact digest
`sha256:705e28e86b82ecb7f6ef309fc37ce3f87d2319499a73556edc415bdaae3d696a`.
The source archive passed nine unit tests in disposable Node 22.14.0 and Node
24.18.1 containers. Vite reported build durations of 5.01 and 4.05 seconds.
These replays did not include the full Hyper-V guest or installed agent lifecycle.

`GameEngineerHarness.CreateShell`, `BoundedWorkspaceShell.RunAsync`,
`WorkspaceProgress.RunShellAsync` and `WorkspaceCheckpoint.InvokeAsync` in the
Engineer repository define the execution boundary. The five-minute timeout
produced no recorded typed timeout in the original attempts.
`AgentRuntimeWorker.RenewLeaseAsync` and `McpAgentRuntimeClient` in Agent.Sdk own
lease renewal. Office's `GuestWorkloadSupervisor`, `GuestBrokerSession` and the
Headquarters `ExecutionBrokerSessionRunner` own process and guest diagnostics.

## Exact guest reproduction results

`artifacts/ticket-vgaebff008a8-33/RuntimeProbe` compiles the unchanged
`GameEngineerHarness`, `BoundedWorkspaceShell`, `WorkspaceCheckpoint` and profile
files through linked source. It consumes SDK 4.4.1 and the same Harness 1.15.0 /
Shell 1.15.0-preview.260722.1 packages as Adrian 4.0.7. A scripted model emits the
restore, unit-test and three original build-command forms. Source comes from the
retained archive. Resource samples are transferred to a host-side collector.

`VmHost` uses the existing Office certification broker and original deployment's
digest-verified Hyper-V helper. It creates a disposable VM using the exact
original guest image, one CPU, no swap and the development environment with the
public IPv4 relay. It does not dispatch work to the installed Adrian, invoke a
live model, change a ticket or exercise the full production work-lease lifecycle.
The writable diagnostic disk is 4 GiB. This is a reproduction of the guest,
production harness and command failure, not full installed-agent certification.

| Observation | 1 GiB guest | 2 GiB guest |
| --- | --- | --- |
| Linux-reported total memory | 917,652 KiB | 1,948,308 KiB |
| Lowest captured available memory | 10,248 KiB | 859,380 KiB |
| Highest captured Node RSS | 560,356 KiB before connection loss | 648,796 KiB |
| Build behavior | Broker channel closed during the first build; no completion receipt | All three command forms returned; builds reported 4.15–4.28 seconds |
| Swap | None | None |
| VM cleanup | Exact diagnostic handle destroyed | Exact diagnostic handle destroyed |

The actual guest supplies Node 22.23.3. `samples.jsonl`, `result.json`, `host.log`
and guest log snapshots are retained in
`artifacts/ticket-vgaebff008a8-33/hyperv-1024-20261017T011841Z/` and
`hyperv-2048-20261017T011841Z/`. `runtime-reproduction-summary.json` summarizes
the captured memory and completion evidence. The helper and image hashes match
the original persisted workload records. Both diagnostic VMs were destroyed.

A separate constrained container control ran all three command forms through the
same production harness successfully. A sleeping-child control, using the same
timeout implementation with a three-second diagnostic deadline, returned
`WorkspaceCommandTimeoutException` and stopped model continuation.

The failed guest had very little memory headroom before build: the first captured
sample reported approximately 530 MiB available. Node's observed requirement in
the successful guest exceeded 630 MiB RSS, alongside npm, esbuild, the agent and
guest services. A container with a 1 GiB memory limit does not include the
Hyper-V guest's kernel and service footprint and is therefore an inadequate
substitute for this capacity check.

The broker loss occurs before the five-minute command deadline, preventing a
typed timeout from explaining the failure to the platform. This is not evidence
that Vite requires minutes to build or that increasing only the deadline helps.
The exact kernel victim and termination mechanism remain unverified: the
additional administrator elevation for a repeat with retained disk/kernel logs
was canceled. No second capture ran. Do not claim a proven kernel OOM kill,
systemd OOM policy or specific deadlock from the available samples.

No installed service, agent package, grant, task state or approval changed. The
local diagnostic host uses the existing test-only certification path; it emits
no new certification evidence and does not replace the production authorization
gate. Verified: October 16, 2026.

## Follow-up implementation register

These items preserve the recommendations from the incident analysis. The implementation
and its remaining installed-runtime acceptance boundary are tracked in
[coding workload resources and recovery](coding-workload-resources.md). Source implementation
does not establish installed guest certification or successful delivery of this ticket.

| Priority | Work | Acceptance evidence |
| --- | --- | --- |
| P0 | Size coding workloads for the complete guest | Use the measured 2 GiB successful case as the initial tested envelope for this task; account for guest/kernel/broker/agent overhead and build peak, expose the actual allocation, and validate capacity before dispatch. A generic agent grant is not a measured build budget. |
| P0 | Preserve infrastructure when a child exhausts memory | Give commands explicit resource boundaries and keep broker/supervision capacity available; retain process/guest termination evidence externally and return a typed infrastructure failure. Test real memory pressure in the certified VM. |
| P0 | Make command supervision survive workload failure | Independently bounded commands and cleanup; typed failure or externally retained process/guest failure; no model continuation or upload racing an uncertain writer. |
| P1 | Route repeated failures to diagnosis | Repeated failure at the same operation enters infrastructure diagnosis rather than another full coding run; source and stage history survive. |
| P1 | Unify recovery state | Producer, developer and orchestrator read the same current queued/running/waiting/exhausted recovery operation; duplicate events and requests have one effect. |
| P1 | Make incident failures actionable | Distinguish stale revision/evidence, revoked authority, invalid report and persistence failures; bounded refresh and retry for stale evidence without weakening authorization. |
| P1 | Carry concrete technical decisions into execution | Resolve the exact Phaser version in structured TD output or an executable prerequisite. A decision that delegates a future kickoff action must not claim the actual pin is resolved. |
| P1 | Capture validation exit codes mechanically | Structured command result receipts retain the actual child exit code; pipelines and appended echo cannot manufacture a validation pass. |
| P1 | Verify the full delivery path | Installed guest and broker exercise implementation, publication, independent technical review, trusted integration and exact-commit QA, including lost responses, duplicate/out-of-order events, stale inputs and runtime replacement. |
| P2 | Reduce retry inference and repeated setup | Durable summaries and validation receipts identify current source, completed checks and the interrupted operation; safe dependency caching avoids repeating unchanged setup. |
| P2 | Standardize browser delivery recipes | Reusable verified source/build/serve recipes; test root URL, required assets, missing/malformed paths and actual browser scene behavior. Preserve independent QA. |

The implemented source package addresses complete-guest resource sizing,
child-resource boundaries and externally retained failure evidence together; real VM
certification and installed delivery acceptance remain pending. A
2 GiB successful replay is evidence for this fixture, not a universal memory
requirement or permission to silently increase every installation's grant.

## Proposed resource sizing policy

The requested 4 GiB coding default and bounded checkpointed growth are now implemented
in source; see [the implementation](coding-workload-resources.md). The earlier investigation
did not change installed grants or defaults. The following preserves the policy rationale. The exact-guest experiment established a
working 2 GiB envelope for this fixture. A 4 GiB general coding default is a
conservative starting proposal, not a result measured by that experiment.

Use per-workload resource profiles rather than a permanent memory size attached
to an employee. Proposed tiers are 2 GiB for measured small workloads, 4 GiB for
unknown coding/build workloads, and 8/16 GiB for heavier work within configured
business, host and owner-approved limits. Keep ordinary coordination separate
from build and test capacity. Reserve host resources across concurrent workers;
queue work when capacity is unavailable.

Agents can inspect manifests, lockfiles, toolchains, test parallelism and prior
results to request a profile and explain the estimate. The trusted scheduler
authorizes and allocates it. Successful peak measurements, guest/control overhead
and a configurable safety margin should govern subsequent requests. Record the
profile by project, command class and relevant toolchain/dependency revision;
changed inputs require reassessment. Treat model estimates as advisory.

Before a heavy command, a capacity request can checkpoint safe source and replace
the VM at a larger tier. After a confirmed memory failure, retain the last
completed checkpoint and permit a bounded larger-tier retry under the existing
budget. Generic lease expiry alone must not trigger memory escalation. Resource
changes need durable operation identities, current authorized reads, admission,
atomic state/outbox persistence and duplicate-safe effects. Downsize on later
runs after sustained measured headroom rather than resizing during an active build.

Longer term, execute builds/tests in separately sized ephemeral workers. That
keeps the coordinator alive when a command exhausts its resource envelope and
allows each project operation to use its own capacity and isolation boundary.
Resume at the interrupted stage without rebuilding the manager conversation.

At investigation time, the code used `AgentInstallationGrant.MemoryMb` in
`AgentRuntimeManager.TryStartAsync`. `AgentRuntimeGlobalSettings` supplied code
defaults of 1,024 MiB, a 2,048 MiB maximum, and a distinct agent-package builder
memory setting; increasing that builder setting does not resize the coding shell.
The implementation now applies a separate coding policy and preserves it through package updates.
`PlatformComputeClient.ProvisionAsync` carries resource-shaped requests, but does
not add an agent-runtime resize operation or override provisioning policy.

Hyper-V Dynamic Memory is another option after provider certification, resource
authorization and scheduler reservation semantics support minimum/startup/maximum
bounds. Current `PowerShellHyperV.ConfigureAsync` explicitly disables it. Microsoft
documents [Dynamic Memory and its buffer/host-capacity behavior](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/dynamic-memory)
and [supported Ubuntu guest features](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/supported-ubuntu-virtual-machines-on-hyper-v).
Validate the exact image under burst demand and host contention before using it
for production recovery. It cannot provide memory that the host does not have.

Developer core fixes belong in both Adrian and Daniel under the root contributor
instructions. Run `scripts/Test-DeveloperAgentCoreParity.ps1`, test and pack each
changed agent, and follow package/version/release-note ordering. Installed-runtime
acceptance remains separate from source-test and package validation.

## Candidate defects separate from the runtime failure

The recovered `server/serve.mjs` declares `urlPath` with `const` and assigns to it
for `/`. HTTP replay returned 500 for `/`, 200 for `/index.html` and 404 for a
missing asset. Change the root-route implementation and verify its actual behavior.

The recovered Dockerfile's runtime stage uses `COPY dist ./dist`, which reads the
source context. The build output exists in the earlier stage and is excluded from
the source snapshot. Copy from the build stage and test a clean image build.
Neither defect explains the original heartbeat loss. The nine pure timestep tests
exercise neither the server nor the Docker recipe or cross-browser acceptance.

## Agent interaction evidence

Attempt 2 began automatically at 00:13:33 UTC on October 17. The Producer's
00:20:57 UTC comment nevertheless reported that no retry was queued or running
and requested a rerun. Both later attempts recovered the source successfully.

Management reports failed with `project-health.unavailable`, then the incident
escalated through the existing reporting hierarchy. The saved code does not
distinguish the handler's several failure branches. Its precise rejection cause
must be investigated before attributing it to stale revision or permissions.

The three implementation attempts made 44 provider calls with 1,449,647 reported
input tokens and 32,210 output tokens. These totals include repeated conversation
input on each call and exclude duplicate queue records; they are not unique-input
counts or a monetary cost estimate.
