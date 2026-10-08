# Chat turn diagnostics

How to find out why a Communications answer failed. The user-visible text is deliberately generic
("The agent couldn't complete that request. Please try again."); the cause is persisted on the turn,
its trace, and agent-side records.

## Turn lifecycle

`ChatTurnWorker` (`src/CSweet.Api/Chat/ChatTurnWorker.cs`) claims a `ChatTurn`, dispatches it to the
target agent as durable `AgentWorkItems` work, relays streamed chunks, then commits the answer. Status,
error code, error message, partial response, and attempt count live on `ChatTurns`; every step writes
a `ChatTurnTraceEvents` row (categories: `system`, `memory`, `model`, `reasoning`, `activity`, `draft`,
`output`).

After a reviewed memory erasure, `ChatTurn.MemoryErasedAt` marks cleared diagnostic copies:
`PartialResponse` and error detail are removed, trace rows are deleted, and `ErrorCode` becomes
`memory.source_erased`. Original conversation messages remain available. `ReviewedMemoryErasure`
fences late diagnostic writes and identity recreation; the immutable erasure receipt records the
operation and pending runtime acknowledgements. Recover its status from **Check a saved erasure
operation** on the Memory page. New turn/trace audit records omit copied chat content; unverifiable
older audit copies block erasure. See [memory hardening](features/agent-memory-hardening.md).

## Failure branch map

| User-visible result | Code | Trigger |
| --- | --- | --- |
| "The agent couldn't complete that request. Please try again." | `turn_failed` | Agent error chunk (`agent_error`, `agent_work_failed`, `agent_progress_unavailable`, cancelled/dead-letter work), runtime not ready, missing installation/provider, empty model response, unresolvable terminal approval message, or any infrastructure exception |
| "...exceeded the N-minute safety limit..." | `timeout` | Turn hard timeout (`ChatTurnOptions.HardTimeout`) |
| "The agent completed its work without providing a response." | `agent_no_response` | Work completed with no final chunk |
| "The recalled context for this queued request is no longer valid. Please retry as a new chat turn." | `memory.recall_stale` | Queue delivery rejected missing, malformed or changed immutable recall evidence before releasing the payload |
| "The agent's memory context changed and its runtime must be replaced." (with review-before-retry guidance) | `memory.runtime_reset` | Delivered work was fenced and settled while retained-context recovery waits for confirmed shutdown |

Before the fallback is written, `ChatTurnWorker` publishes an `agent.error` trace event with the
agent's sanitized failure text and stores `ErrorCode`/`ErrorMessage` on the turn; the final
`turn.completed` trace event repeats the code and detail.

## Where to look

1. Turn dialog in Communications (the thinking/detail button on the message) — trace events, activity
   durations, tool calls, and retry.
2. `ChatTurns` — `Status`, `ErrorCode`, `ErrorMessage`, `PartialResponse`, `Attempt`.
3. `ChatTurnTraceEvents` — ordered trace; `activity.*` rows carry tool names and outputs, and
   `agent.error` carries the agent's own failure text.
4. `AgentWorkItems` — `Status`, `AttemptCount`/`MaximumAttempts`, `Error`
   (`agent-failure:v1;code=<platform.capability.denied|unavailable|validation_failed|agent.invalid_operation|runtime.transport|rate_limited>;diagnosticId=…`).
5. `AgentRunLogs` — LLM/plugin run records (`Status`, `FailureMessage`, `PromptPreview`, `ChatTurnId`).
6. `AgentRuntimeInstances.LogExcerpt` — runtime lifecycle excerpt when the agent never answered.
   Memory-reset validation diagnostics are on `AgentRuntimeEvents.Reason`: server-authored
   `validation`, `receipt`, `work`, and `error` fields identify the rejected check without copying
   recalled content. The runtime's `MemoryResetReasonCode` remains the broad reset classification.
7. Console output: the API process logs `Chat turn {TurnId} failed.` with the exception, and the
   AgentHost process logs platform LLM stream failures. The application has no file log sink, so the
   AppHost console holds the current run's output.

Endpoint equivalents: `GET /communications/hub/chats/{chatId}/turns[/{turnId}[/trace|/events]]`,
`POST .../retry`, `POST .../cancel` (`CommunicationChatTurnEndpoints`).

## Recovery

All supported provider types use the shared response contract described in
[Uniform provider responses](features/provider-response-compatibility.md).
The adapter makes one corrective request for reasoning-only or empty responses,
never replays usable answers or tool calls, and preserves reported usage. A
persistent failure reaches agents as `llm.tool_protocol` or `llm.response_invalid`
with a configuration/retry explanation; it is not a transient provider outage.

`PlatformLlmJobService.StartAsync` records queued calls against their authenticated
work attempt through `InferenceAttribution.CaptureAsync`. `CurrentActivityService.MapAsync`
distinguishes provider-capacity waits from an employee's inbox backlog. With employee
audit access it can name the employee occupying the same provider, without exposing
another board's task content.

`ProviderInferenceGate` admits durable coordination turns, personal commitments,
and stage execution ahead of conversational requests. Priority comes from stored
work, never request telemetry. Admission is FIFO within each class, non-preemptive,
bounded by the configured concurrency/queue limits, and admits one waiting
conversational request after three delivery admissions. A running generation still
occupies its slot until completion or cancellation.

`PlatformLlmCapabilityHandler.StreamAsync` uses `ModelStreamCoalescer.ReadAsync`
to combine ordinary text fragments before audit writes and forwarding. The first
update is immediate; accumulated text flushes on the next update after 500 ms,
32 fragments, 4096 characters, or a message/nontext/terminal boundary. Reasoning,
tools, usage and terminal updates keep their boundaries. Buffered text is retained
before a stream failure, and every forwarded chunk is persisted first.

- Retry a failed turn from the turn dialog; the durable work item allows 3 attempts.
- For `memory.runtime_reset`, the runtime retained memory that is no longer valid, lacks verifiable
  evidence, or reached its receipt limit. `AgentMemoryRuntimeReset.RequestAsync` fences sessions and
  attempts; `AgentWorkInbox.SettleMemoryResetAsync` records a nonretryable result for delivered work.
  Review possible side effects before submitting a new request. Pending work waits for a fresh
  runtime after `AgentRuntimeManager` confirms shutdown. Unknown fleet attempts keep recovery
  blocked; a cancelled assignment alone is not proof that its workload stopped.
- For `memory.recall_stale`, retry as a new turn to prepare current recall. `AgentWorkInbox.ClaimCoreAsync`
  atomically dead-letters the stale work with a protected, content-free failure result and releases no
  payload or lease. `ReadStateAsync` and `WaitForResultAsync` retain the code; `ChatTurnWorker` preserves
  it on the visible failure. Later queued work can proceed. Database availability failures remain
  pending, and an invalid runtime or full receipt budget requires runtime recovery rather than
  being mislabeled as stale work. See [memory hardening](features/agent-memory-hardening.md).
- Cancel a running turn. Pending unmaterialized suggested actions are cancelled with it.
- Agent-side failures (`platform.capability.denied`, `unavailable`, `validation_failed`) usually mean
  the installation grant is missing or stale, or the agent called a capability it was not approved
  for; compare `AgentWorkItems.Error` with the installation's approved grant set.

## Employee timeline

The employee details **Timeline** tab correlates chat traces with messages, work attempts, tool/model calls, and runtime events using the shared audit ledger. See [Employee audit timeline](features/employee-audit-timeline.md) for capture coverage, privileged diagnostic access, and historical limitations.
