# Agent startup recovery and acknowledged inference waiting

## Developer runtime interruption and source preservation

Video Game Engineer 4.0.4 and Software Developer 2.0.3 consume SDK 4.4.1.
`McpAgentRuntimeClient.SendAsync` retries interrupted work-heartbeat requests and
temporary HTTP gateway errors at most three times within the existing control timeout.
It reuses the same authenticated attempt and does not revive expired/revoked leases.
`AgentTicketFeedback.RecordFailureAsync` leaves repeated lease expiry within the
inbox/stage's bounded retry policy rather than prematurely labeling it a repeated coding
issue. Exhaustion still blocks/escalates. `AgentWorkInbox.RecordTicketFailureAsync`
retains the session-expiry explanation from runtime events even when stop recovery
has replaced the final runtime reason.

Both developer harnesses attach `WorkspaceCheckpoint` when a checkpoint callback is
available. Completed source edits and shell commands upload before another tool or
model call. Tools and snapshots are serialized; failed commands also checkpoint partial
edits. Completion-report writes are excluded because SDK snapshots exclude `.csweet`.
Final outcome validation retains its separate checkpoint. A failed upload cancels the
harness and reports preservation failure without replaying the local mutation.

Cancellation does not grant cleanup upload authority. A hard crash during a tool or
upload can still lose changes since the last completed checkpoint. The replacement
runtime materializes that checkpoint through the existing authorized SDK Git client.
No polling loop or additional capability is introduced. Upgrade the host and both agent
packages after active work is checkpointed; source changes do not update an already
running VM.

Observed Adrian failure: last confirmed work heartbeat 2026-10-16 08:41:25 UTC,
lease expiry 08:44:25, MCP session expiry 08:45:06. Retained logs do not establish
whether the initiating failure was connectivity, guest resource pressure, or another
guest failure. These changes close reproduced recovery/preservation gaps, not proof
of that initiating cause. Regression tests reproduce the old failures and exercise
actual confined file/shell tools, SDK upload, interruption and fresh materialization.

The reported Creative Director failure was an MCP startup failure. Logs showed PostgreSQL
entering recovery, an unhandled exception in an event dispatcher stopping AgentHost, and
subsequent MCP initialization requests returning gateway errors. The preceding LLM calls
completed in approximately 15–32 seconds. Slow inference was a separate weakness.

Both event dispatchers now catch iteration failures, log them, and retry durable pending
events using a fresh scope. Normal application shutdown still cancels the loop.

AgentHost acknowledges private inference jobs and bounds admission per provider profile.
`LlmProviderProfile.MaximumConcurrentRequests` defaults to **4**, including existing profiles
upgraded by the `LlmProviderConcurrency` migration. Edit **Settings → LLM Providers → select a
provider → Maximum concurrent requests**; the first-run provider editor exposes the same field.
The value must be at least 1 and is shared by all agents using that profile. It is independent
of the provider server's capacity; set a vLLM profile to 2 if its server supports two concurrent
requests. Profiles pointing to the same server have separate gates.

`LlmProviderProfileService` persists and returns the setting. `PlatformLlmJobService.AcquireProviderAsync`
reads it before admission, and `RefreshProviderCapacitiesAsync` refreshes existing gates in the
five-second maintenance pass, so increases wake queued requests without restarting AgentHost.
`ProviderInferenceGate.UpdateCapacity` lets active requests finish when capacity is lowered and
retains delivery priority and starvation protection. The old
`CSweet:Llm:Queue:MaximumConcurrentRequests` host setting is replaced by the saved profile value;
there is no additional global concurrency cap. `MaximumQueuedRequests` still defaults to 256
per provider. SDK 3.31.1 automatically polls when the host advertises this protocol during a leased
callback. Existing agent chat-client code continues to work. Conversation activity reports
receipt, waiting for a slot, processing, and completion; queue transitions are retained in
agent run diagnostics. Processing indicates dispatch, not provider-confirmed GPU execution.

Authenticated waiting extends only the owning live work attempt and runtime budget. The SDK
callback deadline and chat turn follow that authoritative deadline. Queue time does not count
toward the optional generation limit (`CSweet:Llm:Queue:GenerationTimeoutSeconds`). The default is 0,
meaning no limit: long generations (for example large outputs from local models) are legitimate, and a
positive value is only an explicit operator choice. The provider connection timeout follows the same setting. Cancellation, grant revocation, lease expiration, and infrastructure limits remain
effective. Unpolled requests are cancelled after 60 seconds. The runtime launcher reserves
up to 24 hours of inference waiting through `CSweet:AgentRuntime:InferenceWaitAllowanceSeconds`.
Configure that value consistently in AgentHost and the runtime launcher.

Short start/read/cancel calls avoid holding open an Office broker response during inference;
the broker buffers responses. Result buffering is bounded per request. Jobs are in memory:
a host restart interrupts them and the existing durable work retry path applies. This queue
coordinates one AgentHost process. Multiple replicas need shared admission and sticky routing;
API fallback calls and external provider clients are outside this queue.

## Local rollout

Restart the rebuilt C-Sweet services and reimport/restart the updated agents to negotiate
polling and receive the new workload lifetime. The locally packed releases are SDK 3.31.1,
Creative Director 1.6.2, Producer 2.3.2, Technical Director 2.3.2, and Chief of Staff 2.3.2.
Packages are under `artifacts/adaptive-packages`. No live installation records are changed
by building these packages. A previously suppressed installation must be retried after the
host is healthy through the existing runtime diagnostics controls.

Startup suppression survives an application restart to prevent repeated failure loops.
Updating the hired agent's package or changing its effective configuration (including model
selection) clears its failure counter and makes enabled always-on agents due again. Saving
unchanged defaults does not reset the counter, and disabled agents stay disabled. These
configuration updates schedule work; they do not synchronously launch an agent.

For failures already persisted before this fix, use **Retry startup** in the Communications
unavailable-agent banner, or the existing retry action under **Settings → Agents**. The
action clears suppression and queues startup; it does not guarantee that the underlying
failure is resolved. The banner shows request failures and prevents duplicate clicks while
the request is pending. Normal startup failure limits apply again after a retry.

Regression coverage includes dispatcher recovery, FIFO serialization, acknowledged waiting
beyond the original work budget, queued cancellation, idempotency and ownership, refusal to
revive expired work, SDK deadline propagation, and caller cancellation. SDK documentation
ships `docs/llm-queue.md` in its package, with links from its authoring and runtime guides.
