# Agent startup recovery and acknowledged inference waiting

The reported Creative Director failure was an MCP startup failure. Logs showed PostgreSQL
entering recovery, an unhandled exception in an event dispatcher stopping AgentHost, and
subsequent MCP initialization requests returning gateway errors. The preceding LLM calls
completed in approximately 15–32 seconds. Slow inference was a separate weakness.

Both event dispatchers now catch iteration failures, log them, and retry durable pending
events using a fresh scope. Normal application shutdown still cancels the loop.

AgentHost now acknowledges private inference jobs and admits requests FIFO per provider
profile. `CSweet:Llm:Queue:MaximumConcurrentRequests` defaults to 1; queue capacity defaults
to 256. SDK 3.31.1 automatically polls when the host advertises this protocol during a leased
callback. Existing agent chat-client code continues to work. Conversation activity reports
receipt, waiting for a slot, processing, and completion; queue transitions are retained in
agent run diagnostics. Processing indicates dispatch, not provider-confirmed GPU execution.

Authenticated waiting extends only the owning live work attempt and runtime budget. The SDK
callback deadline and chat turn follow that authoritative deadline. Queue time does not count
toward the separate generation limit (`CSweet:Llm:Queue:GenerationTimeoutSeconds`, default
900). Cancellation, grant revocation, lease expiration, and infrastructure limits remain
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
