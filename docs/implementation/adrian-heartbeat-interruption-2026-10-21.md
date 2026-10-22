# Adrian heartbeat interruption — October 21, 2026

## Finding

Ticket `VG3A716D4AEC-24` (Ball Physics) lost execution attempt 1 after an unhandled
database concurrency exception terminated the shared Office control stream.
Office cancelled its active assignments when that stream ended. Five workload
tunnels, including Adrian's, reset together. The later work-lease and MCP-session
expirations were consequences of this interruption.

The Office Node, RuntimeHost, ExecutionGateway, AgentHost, API and WorkerHost
processes did not restart during this incident. Their process creation times
predate the interruption. This was a connection/lifecycle failure inside running
services.

## Evidence and timeline

Times below are October 21, Pacific daylight time (UTC−07:00).

| Time | Evidence |
| --- | --- |
| 18:37:28.099 | Last successful MCP session renewal for runtime `4e705163-1050-4f71-b8cb-4a40ef47caa6`; expiry became 18:47:28.099. |
| 18:38:28.262 | Last successful work heartbeat for work `ab25eb63-ddb4-45b2-8361-9c2f4eda1aed`; lease expiry became 18:41:28.262. |
| 18:38:40.770 | Windows Application event: `OfficeGateway/Connect` failed with `DbUpdateConcurrencyException`, originating in `ExecutionWorkloadOrchestrator.RenewLeaseAsync` at its `SaveChangesAsync`. |
| 18:38:40.775 | Office Node logged “The office control session ended; reconnecting.” The gRPC result was `Unknown`, “Exception was thrown by handler.” |
| 18:38:40.792–.804 | Five authenticated workload tunnels reset. Adrian's assignment `1bdaaeab-be85-4dbd-a223-f4a970fec8cf`, epoch 2, reset at .796. |
| 18:38:57.882 | Reconnected Office received Adrian's existing assignment at epoch 2. |
| 18:39:27.906 | Epoch 2 was fenced as a newer retry replaced it. Epoch 4 arrived at .913. |
| 18:39:43.936 | Office opened the replacement VM's authenticated guest channel for the same runtime ID. There was no new MCP session establishment for that runtime. |
| 18:41:28.262 | Original work lease expired. |
| 18:47:30.557 | Runtime manager began replacing the runtime after its MCP session expired. |
| 18:48:00.661 | Interrupted stop recovery marked the runtime failed. |
| 18:49:02.804 | Original agent work attempt was recorded as `lease_expired`. |
| 18:49:33.891 | Ticket execution attempt 2 started on a new runtime. |

Windows event excerpts and complete relevant stacks are retained locally in
`artifacts/adrian-heartbeat-2026-10-21/windows-events.json`. Database evidence comes
from `AgentRuntimeEvents`, `McpAgentSessions`, `AgentWorkAttempts`,
`ExecutionWorkloadAssignments`, `ExecutionAssignmentAttempts` and
`WorkExecutionAttempts`. No database or installed runtime was modified.

## Source explanation

1. `ExecutionWorkloadOrchestrator.RenewLeaseAsync` reads a concurrency-protected
   assignment and saves its renewed lease without handling
   `DbUpdateConcurrencyException`. `OfficeGatewayService.Connect` lets that
   exception terminate the shared stream. Assignment concurrency tokens include
   `AssignmentTokenHash`, `ArtifactGrantTransferHash` and `FencingEpoch` in
   `CSweetDbContext.OnModelCreating`. The retained exception does **not** identify
   the conflicting assignment, competing writer or changed token. Do not claim a
   particular token race as proven.
2. `OfficeWorker.RunControlSessionAsync` cancels every entry in
   `_activeAssignments` in its `finally` block. `ExecuteAssignmentAsync` cancels
   the guest tunnel and invokes stop-journal cleanup. A stream error therefore
   interrupts unrelated active work, even though Office immediately reconnects.
3. `ExecutionWorkloadOrchestrator.FenceExpiredAsync` requeues expired assignments
   and increments their fencing epoch. The assignment-attempt records show two
   different provider instances for Adrian: epoch 2 used
   `d10cf4e922b045c982eb9d9a5d8d917d`; epoch 4 used
   `051b19a65bd944c5a16913b0d5a84a00`.
4. `ExecutionBrokerSessionRunner.RunRuntimeAsync` waits for the runtime record to
   become `WaitingForMcpSession` before starting the guest handshake and workload.
   It rejects terminal states but otherwise waits indefinitely. Adrian's reused
   runtime remained `Running`, with its old MCP session, throughout the assignment
   replacement. The replacement guest channel therefore could not launch a new
   agent process. This explains the open guest channel at 18:39:43 with no renewed
   work heartbeats or new MCP session until the old runtime was retired.

The final original model call completed server-side at 18:39:23, after the tunnel
loss. A completed provider log does not establish that the interrupted agent
received the response or continued execution.

## Repair requirements

- Contain renewal conflicts within the affected assignment operation. Reload and
  revalidate current authority before a bounded retry; never renew a revoked,
  expired or superseded epoch. An ordinary concurrency conflict must not terminate
  the Office-wide control stream.
- Review control-stream disconnect behavior so transient connection loss does not
  unnecessarily destroy all workloads. Preserve current authorization and lease
  fencing; retaining a process must not grant it new execution authority.
- Make runtime-assignment replacement and runtime/MCP lifecycle agree. A new VM
  must receive an explicit valid startup/recovery path, or the old runtime must be
  retired promptly and its durable work resumed through a fresh runtime. Do not
  simply admit `Running` into the handshake loop or reuse an old boot credential.
- Cover a lease-renewal race, unrelated active assignments, and replacement after
  an established MCP session in regressions. Verify recovery with an authorized
  live workload after releasing the applicable components.

## Implemented recovery

Headquarters `ExecutionWorkloadOrchestrator.RenewLeaseAsync` now contains optimistic
concurrency conflicts with up to three attempts. Each retry reloads assignment,
Office, expiry and internet-grant authority. Exhaustion rejects only the affected
renewal; `OfficeGatewayService.Connect` continues serving the other assignments.

Office **0.11.1** `OfficeWorker.RunControlConnectionsAsync` keeps its gRPC channel
and active workload lifetimes across transient control-call failures. It retries
the control connection after five seconds. `ReconnectingControlWriter` serializes
status/lease writes, waits up to twenty seconds for a replacement writer, and
protects a new connection from stale cleanup. Invalid envelopes, identity
rejection, assignment fencing and process shutdown still stop the applicable
workloads. A disconnected failure does not wait another reconnect window before
journaled teardown. Actual workload tunnel loss still ends that workload.

`ExecutionWorkloadOrchestrator.FenceExpiredAsync` retires expired **runtime**
assignments instead of creating a new VM under the old runtime's consumed boot
credentials. Builder/toolchain retry policies stay distinct. Confirmed teardown
gates fresh runtime creation through the existing runtime manager. Durable work
leases then recover through the inbox and orchestration retry policy, retaining
saved inputs and existing checkpoint storage. This is recovery of interrupted
work, not a replay of already committed results. Inbox discovery can settle an
expired lease on one pass and claim its newly Pending work on the next pass;
the normal work-lease expiry is three minutes.

`ExecutionBrokerSessionRunner.WaitForRuntimeStartupAsync` admits only a fresh
`WaitingForMcpSession` runtime, waits at most thirty seconds while it is
`Starting`, and immediately rejects `Running`, `Stopping` and terminal records.
It does not reuse a boot grant to recover an established session.

## Verification and rollout

- `ExecutionLeaseRenewalRecoveryTests`: actual EF concurrency races, current
  authority checks, bounded conflict retries and continued gateway service for
  another assignment.
- `ExecutionRuntimeReplacementTests`: old runtime assignment is fenced and not
  redispatched, teardown confirmation remains required, and stale broker startup
  is rejected without waiting for session expiry.
- `AgentRuntimeManagerAssignmentRecoveryTests`: an OnDemand runtime retires,
  revokes its old session, creates a fresh runtime/tick/boot credential, and
  establishes its new MCP session.
- `AgentWorkInboxTests.RuntimeReplacement`: expired durable work is reclaimed
  under the fresh runtime with the same inputs and a new lease; late old-runtime
  renewal/completion cannot overwrite it; the replacement completes successfully.
- Office `OfficeControlReconnectTests`: two signed runtime assignments execute
  through the Node loop with simulated provider/gRPC transports. Control loss
  preserves both VMs/tunnels, duplicate delivery creates no extra VM, targeted
  fencing stops one workload, rejected identity stops both, and waits are bounded.
- `ExecutionLeaseRenewalRecoveryPostgresTests`: three real PostgreSQL race cases
  use loopback-only, randomly named disposable databases. They exercise token
  rotation, epoch changes and expiry, and delete each test database afterward.

Focused Headquarters verification passed **180** tests. The PostgreSQL verification
separately passed all **three** race cases plus the two runtime/inbox recovery
checks. Office's independent Release build passed with
`UseLocalOfficeContracts=false`; **244** Office tests passed.

Broader verification also found three existing `WorkDispatchRecoveryTests`
failures (`Waiting_card_is_repaired_then_moves_only_when_inbox_is_claimed`,
`Terminal_completion_is_durable_even_when_a_dependent_dispatch_fails`, and
`Blocked_result_replaces_previous_attempt_error_on_stage_and_ticket`). All three
also fail in the preexisting Debug binaries. Office's
`CertificationCommandTests.TimeoutRetainsOutputWrittenBeforeTheCommandWasKilled`
also fails in its preexisting Release binaries; it was excluded from the final
244-test Office run after that baseline check. These failures were not changed.
Logs are in Headquarters `artifacts/heartbeat-*.log` and Office
`artifacts/heartbeat-recovery/`.

These checks verify source recovery with real PostgreSQL and simulated VM/tunnel
transports. They do not certify live Hyper-V replacement or deploy the fix to
the installed services. Upgrade Headquarters first, then drain Office to zero
active assignments and deploy the versioned Office release through its supported
upgrade flow. No agent package, stored manifest, runtime payload or application
database was patched to apply these changes.
