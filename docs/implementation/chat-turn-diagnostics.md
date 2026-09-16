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

## Failure branch map

| User-visible result | Code | Trigger |
| --- | --- | --- |
| "The agent couldn't complete that request. Please try again." | `turn_failed` | Agent error chunk (`agent_error`, `agent_work_failed`, `agent_progress_unavailable`, cancelled/dead-letter work), runtime not ready, missing installation/provider, empty model response, unresolvable terminal approval message, or any infrastructure exception |
| "...exceeded the N-minute safety limit..." | `timeout` | Turn hard timeout (`ChatTurnOptions.HardTimeout`) |
| "The agent completed its work without providing a response." | `agent_no_response` | Work completed with no final chunk |

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
7. Console output: the API process logs `Chat turn {TurnId} failed.` with the exception, and the
   AgentHost process logs platform LLM stream failures. The application has no file log sink, so the
   AppHost console holds the current run's output.

Endpoint equivalents: `GET /communications/hub/chats/{chatId}/turns[/{turnId}[/trace|/events]]`,
`POST .../retry`, `POST .../cancel` (`CommunicationChatTurnEndpoints`).

## Recovery

- Retry a failed turn from the turn dialog; the durable work item allows 3 attempts.
- Cancel a running turn. Pending unmaterialized suggested actions are cancelled with it.
- Agent-side failures (`platform.capability.denied`, `unavailable`, `validation_failed`) usually mean
  the installation grant is missing or stale, or the agent called a capability it was not approved
  for; compare `AgentWorkItems.Error` with the installation's approved grant set.
