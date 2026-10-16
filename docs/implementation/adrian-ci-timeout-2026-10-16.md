# Adrian CI timeout, October 16, 2026

## Recorded evidence

The `work.execution.run` inbox item `1606c66c-b9c1-45d0-8892-75bfc1f4de88` was executing
`[prisma-scaffold] Project scaffold & core physics` (`f89b4fd5-5ed7-442c-8d70-51b500d255dc`).
At 17:04:49 UTC its last `WorkspaceProgress` receipt was operation 41, command started.
The preceding tool call was `npm run ci 2>&1 | tail -35; echo "CI_EXIT=${PIPESTATUS[0]}"`.
No command result followed. The last model call had completed normally.

The runtime still appeared Running, but work heartbeats stopped at 17:17 UTC. Its MCP
session expired and runtime replacement began at 17:23:43 UTC. Lease recovery at
17:24:53 UTC blocked the ticket as a repeated issue. This was infrastructure evidence,
not proof of a repeated coding error. The stopped guest retained no process-tree or
per-stage output sufficient to identify the original hung CI child.

The saved source archive from 17:04:42 UTC was replayed in a disposable toolchain
container. TypeScript, ESLint, Vite, and all 39 Vitest tests passed. Four further runs
of the exact pipeline through the installed shell package, with the original 1 CPU /
1 GiB limits, also passed. These reproductions do not identify which child stopped in
the original guest. Do not report a specific CI child as the proven cause.

## Timeout and recovery changes

`GameEngineerHarness.CreateShell` and `SoftwareDeveloperHarness.CreateShell` now use
stateless confined `LocalShellExecutor` instances behind `BoundedWorkspaceShell`.
The outer five-minute deadline includes the framework's command await; cleanup has
an independent ten-second grace. Unresponsive cleanup poisons that executor.
`RunImplementationAsync` cancels model continuation and retains the last completed
`WorkspaceCheckpoint`, without racing a new upload against an uncertain command.
Caller cancellation remains cancellation.

`AssignedDevelopmentService` returns the typed `workspace.command.timeout` failure.
`AgentWorkInbox.FailCoreAsync` commits the attempt failure and runtime Stopping event
together. The old runtime cannot claim further work; normal isolated-runtime
reconciliation replaces it. Existing inbox/stage attempt budgets still bound retries.
`AgentWorkFailure.IsWorkspaceCommandTimeout` also recognizes the SDK's exact typed
exception envelope for event callbacks. Exhausted retries retain management escalation.
`AgentTicketFeedback.RecordFailureAsync` does not mistake a recoverable timeout or
lease loss for a repeated coding mistake while its bounded retry budget remains.

## Management visibility

`ProjectHealthReader.ObserveAsync` detects fifteen minutes without attempt-scoped
progress even when its lease is renewed or other project work is active. A recorded
provider queue/live model call is distinguished from a silent workspace operation;
model completion supplies a fresh progress baseline. Failures retain the exact ticket
and existing incident fingerprint, so handoff deadlines are not reset by heartbeats.

`ProjectHealthService.DeliverAsync` sends one informational Communications report and
notification when an unresolved incident reaches a human owner, even if no manager
produced a decision proposal. This does not create approval choices or grant repairs.
Stale choices are withdrawn. Current manager recovery remains with the manager;
confirmed recovery dismisses the informational notification. The normal reporting
hierarchy and its persisted handoff deadlines remain authoritative.

## Verification and local rollout

`WorkspaceShellTests` covers deadline enforcement, unresponsive cleanup, caller
cancellation, preventing model continuation, and checkpoint preservation. The real
nested-process test also passes on Linux. `AgentWorkInboxTests` covers fencing the
old runtime and retrying only with a replacement. `ProjectHealthTests.StalledExecution`
covers renewed leases, provider waits, ticket attribution, deduplication, and CEO
delivery without invented decisions. `Test-DeveloperAgentCoreParity.ps1` includes
the new shell regressions.

Agent versions are Adrian 4.0.6 and Daniel 2.0.5. Adrian's local installation was
upgraded through `AgentDefinitionService.UpdateAsync` after validating and signing
the Linux artifact with the existing artifact store. Permission parity was checked;
a serializable guard required Adrian to be the sole affected installation. The
development source archive is retained with SHA-256
`f677d629e2cc0139eb9b058a34b001d6e86bcc35bc05fe52982160b83fc3c913`.
This local artifact is explicitly recorded as development source, not a remote Git
release. The published package history was not changed.
