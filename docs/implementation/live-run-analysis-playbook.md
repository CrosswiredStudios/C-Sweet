# Live run analysis playbook

How to analyze a company run while it is in progress: where the data is, how to read it, how to
get from a symptom to the line of code, and how to record the result. Written after the Qwen Games
breakout analysis on 2026-11-09. Keep it current; each pass should make the next one shorter.

## Access

| What | Where |
|---|---|
| Web app | `https://localhost:7125` (https profile) or `http://localhost:5097` (http profile) |
| API | `https://localhost:7033` (https) or `http://localhost:5149` (http). `GET /health` returns `Healthy` |
| Database | PostgreSQL in Docker on a random host port. Not reachable from a sandboxed shell |

Notes for AI assistants:

- Read run data through the API from a signed-in browser page:
  `fetch('https://localhost:7033' + path, { credentials: 'include' })`. Use GET requests only.
- The Claude desktop app's built-in browser pane blocks the app's calls to the API port
  (`net::ERR_BLOCKED_BY_CLIENT`), so sign-in shows "C-Sweet can't reach its server". This is the
  pane, not C-Sweet. Use the owner's signed-in Chrome instead.
- Tool output cuts long strings at about 1,000 characters. Return long text in chunks.
- No .NET SDK is available in assistant sandboxes. Code written there is uncompiled. Say so in the
  hand-off and write `Validation: not yet run` in the release notes.
- Connect every agent repository the run uses before starting. The Video Game Director's source
  was not connected on 2026-11-09, which left its template and resume rules unverified.

## Read endpoints

`{org}` is the organization id from `GET /api/core/organizations`.

| Question | Endpoint |
|---|---|
| Who is on the team | `/api/core/organizations/{org}/users`, `/api/organizations/{org}/roles` |
| What is each agent doing now | `/api/core/organizations/{org}/dashboard/activity` |
| CEO-visible chats | `/api/organizations/{org}/communications/hub`, then `/hub/chats/{chat}/messages` |
| Agent-to-agent chats | Same two calls with `?perspectiveOrganizationUserId={managerId}` |
| Collaboration state | `/api/organizations/{org}/communications/hub/chats/{chat}/coordination-sessions` (`status`, `canRetry`, turns) |
| Boards and sprints | `/api/organizations/{org}/work/boards`, `/work/boards/{board}`, `/work/boards/{board}/sprints` |
| Personal boards | `/api/organizations/{org}/work/personal-todos/` |
| Everything an agent did | `/api/core/organizations/{org}/employees/{id}/timeline?from=…&cursor=…` (50 per page; follow `nextCursor`) |
| One event in full | `/api/core/organizations/{org}/employees/{id}/timeline/{eventId}` |
| Tokens per agent | `/api/organizations/{org}/analytics/inference` |
| Tokens and effort per work item | `/api/organizations/{org}/analytics/efficiency` |
| One document | `/api/organizations/{org}/documents/{artifactId}` (ids are on chat messages under `artifacts`) |

Known defect: the document list `GET /api/organizations/{org}/documents` returned 500 on 2026-11-09.

## Procedure

1. **Snapshot.** Team, activity, boards, token totals, and the time. State how old the company is.
2. **Read the conversations.** The CEO's chats first, then each manager's perspective. Note every
   decision asked of the CEO, every raw identifier or JSON shown to them, and every repeated message.
3. **Find where progress stopped.** The activity feed's `currentAction` and the coordination
   sessions' `status` name the blocker. `Blocked` sessions do not recover on their own; only
   `Failed` sessions with a transient cause do (`AgentCoordinationService.RecoverTransientFailuresAsync`).
4. **Page each agent's timeline and count by `eventType`.** Ignore `agent.capability.started`,
   `agent.work.claimed/leased/pending/attempt.*` and `chat.trace.recorded` when reading; keep them
   when counting. Look for `outcome: Failed`, `agent.runtime.failed`, and
   `chat.turn.completedwithwarnings`.
5. **Open the failed event.** `payloadContent` holds the exact arguments the agent sent.
6. **Open the model call that produced it.** The `model.call.started` event with outcome `Running`
   holds the full request, including any template. The `model.response` event holds
   `modelResponse.text` and the model's reasoning in `modelResponse.contents`.
7. **Compare three values:** what the template contained, what the model returned, what was
   submitted. The step where the value changes is the cause.
8. **Map to source.** Search the platform for the error text, then read the agent's prompt and its
   local validator.

## Root-cause questions

Answer these before proposing a fix:

- Was the correct value in the model's input?
- Did the model change it, and did its instructions tell it to?
- Was the model told the rule it broke?
- Does the agent's local validation match the platform's?
- Is a result cached or frozen before the platform accepts it?
- After the failure, which agent or person owns the next step, and can they actually take it?
- After the fix ships, what re-runs the blocked step?

## Numbers to record every pass

| Metric | Source |
|---|---|
| Minutes since last real progress | Chats and activity |
| Model calls, input and output tokens, per agent | `analytics/inference` |
| Capability calls per model call, per agent | Timeline counts |
| `attention.review-due` claims per agent | Timeline `agent.work.claimed` summaries |
| Runtime starts, cancels and failures per agent | Timeline `agent.runtime.*` |
| Cards on each personal board, system versus real | `personal-todos` |
| Decisions asked of the CEO before work starts | CEO chats |

## Recording results

- One project document per pass: `claude/<company>-<game>-journey-pass-<n>-<date>.md`.
- One document per root cause that needed code: `claude/<short-name>-root-cause-<date>.md`.
- Keep the findings tracker for the run current; it carries status between passes.
- Recurring contract or recovery behavior belongs in this repository under
  `docs/implementation/`, next to the feature it describes.
- Mark anything not verified as not verified, with the reason.
