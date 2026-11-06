# Harness workspace improvements

Accepted scope: items 2–7 from the harness integration proposal. Session takeover, session import/export, and cross-harness continuation are explicitly excluded.

For the end-to-end runtime path (VM channel, dispatch order, routing, fallbacks and failure codes) see [Agent harness and routing](agent-harness-and-routing.md).

## Delivery map

| Capability | Integration boundary | Acceptance |
| --- | --- | --- |
| Run inspector | Existing authorized current-activity feeds, immutable audit identities and canonical work references | A real activity opens a run timeline/graph; selected nodes expose their evidence; reconnect refreshes current authorized state. No inferred causal edges or private reasoning are invented. |
| Context efficiency | The platform LLM request boundary and protected, installation-scoped tool-result storage | Bounded tool-result spills remain retrievable; transformations preserve tool-call/result structure, current instructions and recent turns; source changes invalidate reuse. Context accounting records actual transformations. |
| Model routing and budgets | Approved installation policy and provider dispatch | Explicit stage routes, permitted fallback, token/cost reservations and settlements; retries/delegates share canonical work budgets; unknown pricing cannot bypass a configured monetary ceiling. Existing default behavior remains when unconfigured. |
| Versioned skills | Immutable business-owned skill revisions, explicit installation assignments and platform prompt preparation | Pin instructions and required capabilities to a digest; record the exact revision used on a run; edits create revisions; disabling a skill prevents later injection. |
| Typed judgments | Separately configured bounded inference, deterministic result validation and evidence records | Code supplies candidates; only matching typed answers survive; judgments are advisory and cannot grant permissions, approve mutations or bypass budgets. |
| Doctor | Current provider/runtime/compute/setup/grant/checkpoint/approval reads | An authorized user sees concrete blockers and links to existing repair workflows; checking health does not launch workloads or change authority. |

## Implementation rules

- Reuse canonical activity, execution, memory authorization, audit, provider transport and notification services.
- Persist mutations and notification/audit outboxes atomically. Realtime events are wake hints; reads recheck authorization.
- Never cache or replay side effects as a context optimization. Preserve originals and the provenance of any shortened prompt representation.
- Recheck approved provider/skill/policy revisions at dispatch, including retries. Existing memory-erasure and work-lease fences remain in force.
- Defaults must preserve existing deployments. New routing, compaction and judgment behavior requires explicit configuration.
- Changes to sibling packages or developer agent cores require their versioning, parity and release checks from `AGENTS.md`.

## Verification

Exercise tenant and employee access, disabled/revoked configuration, stale revisions, protocol preservation, budget contention, cancellation, replay and reconnect behavior. Use real service wiring and PostgreSQL tests for atomic reservation/claim behavior. Record broad-suite failures separately from these additions.

## Code map and supported behavior

- `RunInspectorService.ReadAsync` calls `CurrentActivityService.FeedAsync` / `ModelFeedAsync` before projecting model records. It uses exact model and function-call identifiers for edges. `RunInspectorNode.Merge` deduplicates and orders stream segments across pages. Employee diagnostics and canonical board access remain mandatory.
- `HarnessWorkspaceEndpoints` requires an interactive human session and `AgentGovernanceAccess.CanManageAsync`. `HarnessWorkspaceService.SaveAsync` uses expected revisions, validates enabled providers and capability-compatible skill pins, and saves its audit and realtime outboxes in the same transaction. `PublishAsync` creates a new business skill revision. `DisableSkillAsync` permits the publisher or business owner; it never edits published instructions.
- `HarnessRuntime.PrepareAsync` resolves stage keys from durable `WorkExecutionAttempt.StageExecution.StageKey`, with `Default` as the fallback route. `Judgment` is selected only by the trusted judgment handler. Stage names must match the orchestration policy's actual keys. Providers change only through an explicit saved route. A route may list up to three approved fallback targets, tried in order only when a target is unavailable before any output (see [Agent harness and routing](agent-harness-and-routing.md#fallbacks)).
- `HarnessBudgetSession.ReserveAsync` serializes each actual transport attempt using a PostgreSQL transaction advisory lock. Canonical descendants share their root task account and inherit the assigned root agent's ceilings. A child can tighten a ceiling, never raise it. Standalone work without canonical attribution uses a durable work account or installation account. These ceilings govern agent requests through `PlatformLlmCapabilityHandler`; other platform model workflows are outside this integration boundary.
- Token reservations use a conservative input upper bound plus the full configured output allowance. Monetary reservations require operator-supplied USD prices per million input/output tokens. Cached input is conservatively priced as ordinary input. Unknown prices cannot enter an account with a monetary ceiling; prior unpriced usage prevents retroactively claiming a verified monetary ceiling. Provider retries reserve again. Only the successful final attempt with complete usage settles down to reported consumption; failed, cancelled, abandoned and unreported usage remains reserved.
- `HarnessContextOptimizer.Compact` shortens exact duplicate function/argument/result representations inside one request, retaining the later original and all protocol envelopes. It never caches tool reads or assumes mutable sources remain current. Context checking leaves the configured output allowance free. `Spill` shortens old large results only when `platform.harness.context.read` is explicitly granted and the request is eligible for ordinary protected model evidence. Memory-derived/content-free requests do not spill. `HarnessContextCapabilityHandler` reads bounded sanitized snapshots from existing protected audit evidence, checking the exact installation, runtime/tick, live work attempt, source authority, erasure state and evidence integrity. It introduces no additional content store.
- Decision compaction (`HarnessDecisionCompactor`, `HarnessDecisionClient`, `HarnessDecisionEngineResolver` in `CSweet.Infrastructure/Agents`) is optional and inert unless an engine resolves. It runs in `PlatformLlmCapabilityHandler.StreamRouteAsync` only under context pressure and only for requests eligible for ordinary protected evidence; see [Agent harness and routing](agent-harness-and-routing.md#3-model-inference-and-routing) step 4. The policy flag `HarnessPolicy.DecisionCompaction` (default on, stored in `PolicyJson`, no migration) matters only with `CompactContext`; the Models & budgets tab disables its switch while compaction is off.

### Decision-model configuration

Section `CSweet:Harness:DecisionModel` (`HarnessDecisionModelOptions`, environment `CSweet__Harness__DecisionModel__<Key>`), read once per process by both the API (Doctor/status display) and the AgentHost (compaction), so configure both:

| Key | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | `false` makes the engine unavailable. |
| `BaseUrl` | — | HTTPS, or HTTP on loopback only; no userinfo, query or fragment. |
| `Model` | `jev-latest` | Or the fallback engine's model. |
| `ApiKey` | — | Never logged, audited or shown. |
| `KeyEnv` | — | Names the variable holding the key; when set it is the only key source. |
| `TimeoutMs` | 20000 | 1000–180000, includes retry backoff. |
| `Retries` | 2 | Max 5; 429/529/5xx/transport only, backoff 200·3ⁿ ms capped at 5 s. |
| `MaxQuestionsPerRequest` / `MaxRequestTokens` | 24 / 30000 | Batch limits; a state that leaves no room fails without sending. |
| `MaxParallel` | 4 | 1–16 concurrent batches. |
| `MaxStateTokens` | 25000 | Transcript is fitted in stages (args 1000→200→60, texts head+tail 400, omitted, dropped); else skipped. |
| `PreserveRecent` / `KeepThreshold` / `TruncateHeadChars` | 6 / 0.5 / 300 | |

Resolution order (`HarnessDecisionEngineResolver.Resolve`): endpoint from `CSWEET_JEV_LOCAL_URL` (a bare origin or `/v1` gets `/v1/systemone`), then `BaseUrl`, then an automatic fallback engine, then `https://api.typesafe.ai/v1/systemone`. Key from `KeyEnv`, else none for loopback, else `ApiKey`, else (only for `api.typesafe.ai`) `TYPESAFE_API_KEY`, `TYPESAFE_KEY`, `JEV_API_KEY`. Fallback engines apply only when nothing above is configured and no TypeSafe key exists: `SUBQ_API_KEY` (`api.subq.ai`, `subq-snap-preview`), `OPENROUTER_API_KEY` (`openrouter.ai/api/alpha/decisions`, `inception/mercury-decide`), `LIQUID_AI_API_KEY` (`api.liquid.ai`, `d1-ultra`). Inside a container a loopback engine maps to `host.docker.internal`, as for local LLM providers.

**Privacy.** When it runs, previews of old tool calls and results (arguments and results cut to 1,200 characters, secret-shaped content replaced by `[redacted: this looked like a credential and was not sent]`), a transcript outline and the last three user turns are sent to the configured decision engine. Originals stay in protected evidence (`RequestEvidenceJson`). Memory-derived and content-free requests are never sent. Engine usage is recorded in `InferenceSettingsJson.decisionCompaction`, not charged to task budgets.

- `HarnessJudgmentCapabilityHandler` requires both `platform.harness.judge` and ordinary model inference grants plus a saved `Judgment` route. `HarnessJudgments.Options` builds the fixed Relevance, Risk and Completeness choice sets in code. Only one exact JSON choice is accepted. No judgment output enters authorization or approval logic. Calls use the normal dispatch and budget path, have a configurable 64–4096 output-token allowance (512 by default), and record format validity. Format validity is not a claim of semantic accuracy.
- Doctor uses `IAgentInteractiveRuntimeService.GetStatusAsync`, never `EnsureReadyAsync`, and reads provider credentials, pool heartbeats, setup/grants, pending approvals and resource recovery state. It makes no provider requests and starts no runtime. Its configuration-presence checks do not certify workload placement or provider compatibility.

The execution workspace is linked from an agent's Configuration tab. `tools/CSweet.Promo.Demo/FixtureHandler.Harness.cs` supports a clearly labeled in-memory design preview; it is not evidence of real provider execution or production readiness.

No changes to installed runtime packages or sibling agent cores are part of this implementation. Packages must explicitly request new harness capabilities through their normal release/grant flow.
