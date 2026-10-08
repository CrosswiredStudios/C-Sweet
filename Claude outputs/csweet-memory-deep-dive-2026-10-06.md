# CSweet.Memory deep dive — approach, retrieval quality, and context cost

Date: 2026-10-06. Revised the same day after review (see §8 for what changed and why).

> **Evidence status.** Findings in §§1–4 and §8 are verified by reading the code at the referenced lines. Statements about production *behaviour* — "recall mostly misses", enrichment backlog growth, comparative quality against commercial products — are inferences from the code, not measurements; no live recall rates were sampled. The 20 library tests pass; none exercise PostgreSQL or the cross-repo integration issues. Scope: `CSweet.Memory` (0.1.x, all six packages), the platform wiring in `csweet` (`PlatformMemoryCapabilityHandler`, `AgentMemoryService`, `MemoryCaptureWorker`, `ChatTurnWorker`/`ChatPromptPolicy`), the SDK's `PlatformMemoryClient`, and the only agent that consumes memory today (`CSweet.Agent.CreativeDirector.VideoGame`).

## Summary

The design of CSweet.Memory is strong and, in several respects, ahead of the commercial memory products: immutable provenance-bearing episodes, bi-temporal claims with supersession, trust tiers and confirmation states, sensitivity-based redaction, deny-by-default authorization, namespaces that mirror the org (organization / team / role / employee / user-relationship / case), and an approval-gated knowledge-transfer workflow for replacing an employee. That is the right shape for a workforce whose members need to accumulate operating knowledge over months.

The implementation underneath that design is not yet delivering it. Three things dominate:

1. Retrieval is effectively keyword-only, and in production it mostly misses. The vector channel is dead end-to-end (no embeddings are ever written, no query embedder is configured, and PostgreSQL has no pgvector so it would be a full partition scan anyway). The semantic, graph and procedural channels match with `LIKE '%<entire query>%'`, which never matches a natural-language query. PostgreSQL full-text uses `plainto_tsquery('simple', …)`, which ANDs every word, so a multi-word query must contain every term verbatim to hit. The net effect is that `MemoryEngine.RecallAsync` as the Creative Director calls it (long descriptive queries, through the broker to Postgres) very likely returns nothing most of the time, and the platform chat path only works because `AgentMemoryService` splits the query into single terms and fires up to 7 queries × 3 namespaces × 5 channels per turn.

2. What does get injected is mostly raw transcript. Every user and assistant message becomes an episode, verbatim, and episodic full-text is the one channel that reliably fires. So "memory" in the prompt is largely old chat lines (up to 8 items, 1,200 chars each, 6,000-char cap on the platform path; up to 2,000 estimated tokens per `RecallAsync` on the engine path, and the Creative Director stacks two recalls plus the platform's own injection when reached via chat). There is no summarization, consolidation, recency weighting, or usage-based reranking, so the value per token is low and will degrade as episodes accumulate.

3. The learning loop is open. Entities, claims, edges and procedures are extracted (one LLM call per turn pair, with no view of existing memory, so no dedupe/update), but on the platform path claims are never superseded (contradictions accumulate), procedures are born `Pending` and nothing confirms them, the `Core` block layer is never written by anyone, `MemoryUse` is recorded as `Supplied` for everything and no one records `Cited`/`Accepted`/`Rejected` or uses it in ranking, and explicit proposals from agents (plus applied knowledge transfers) are never enriched at all because enrichment only runs over conversation messages.

There is also a cluster of policy-enforcement gaps that should be fixed before more agents adopt memory: sensitivity redaction runs inside the agent's own process using an attribute the agent sets on itself (`memory.maxSensitivity`), while the broker's `search`, `get-claim`, `list-claims`, `export` and `get-knowledge-transfer` all return full content; broker writes accept agent-supplied `Trust`, `Confirmation` and transfer `Status`; and `MemoryPartition.Key` can collide across differently-shaped partitions, which undermines the broker's per-field checks (§8.2).

A further integration finding changes the priority order: the platform chat path and the Creative Director do not share memory at all today, because they build namespaces with different application segments (`"csweet"` vs the installation id) and therefore different partition keys (§8.1). Fixing retrieval alone would not make them see each other's knowledge.

Only the Creative Director and the platform chat path use memory today. The agents that do the actual sprint work (Producer, Software Developer, QA, Technical Director) have no memory at all, which is the biggest gap relative to the goal of memory being how agents "operate within the business long term."

The rest of this document gives the evidence, then a prioritized plan.

## 1. What the system is (architecture map)

**Model** (`CSweet.Memory.Abstractions`). Five layers: `Working`, `Episodic`, `Semantic`, `Procedural`, `Core`. Episodes are immutable, checksummed, idempotent, with `OccurredAt`/`RecordedAt`, sensitivity, optional expiry/legal hold and `OperationalReferences` back to authoritative platform records. Claims are subject–predicate–object/value triples with `Trust`, `Confirmation`, `Sensitivity`, `Confidence`, `Importance`, `ValidFrom`/`ValidTo`, `SupersedesClaimId`, `ExtractorVersion` and a `Kind` (Fact, Observation, Decision, Commitment, WorkOutcome, Handoff, Feedback, Failure, SkillEvidence, OpenQuestion). Edges are temporal relationships; `Blocks` are Letta-style pinned core memory; `ProceduralMemory` holds versioned, confirmable procedures; `MemoryUse` records each supply/citation/acceptance. This is a bi-temporal property graph in relational tables, conceptually very close to Zep/Graphiti, with a richer governance model.

**Engine** (`MemoryEngine`). `IngestAsync` authorizes, appends the episode, and enqueues enrichment. `RecallAsync` authorizes, optionally embeds the query, resolves readable namespaces, runs `IMemoryStore.SearchAsync` per namespace (limit 30), fuses with Reciprocal Rank Fusion (k=60, trust multiplier 1 + 0.05·tier, confirmation multiplier 0 / 0.75 / 1), redacts, packs by estimated tokens (chars/4) into the budget, records `Supplied` uses, and renders an escaped `<memory_context trust="untrusted">` bullet list. Also implements correction/supersession, confirm, export, delete, and the three-step knowledge transfer.

**Enrichment** (`MemoryEnrichmentWorker` + `MicrosoftExtensionsAIMemoryEnricher`). Bounded channel, single reader, one chat call per episode with a JSON-shape prompt, optional embedding. Resolves entities by application key → canonical name → alias scan, prefixes non-protected types with `learned:`, detects conflicting claims (same subject + predicate, different value) and supersedes them, writes edges, writes procedures as `Pending`.

**Stores.** `SqliteMemoryStore` (FTS5 + bm25, recursive CTE graph walk depth 3, in-process cosine over all embeddings in the partition) and `PostgreSqlMemoryStore` (generated `tsvector` with GIN index, `ts_rank_cd`, same CTE, same in-process cosine). Both persist each record as JSON `payload` plus a few indexed columns. `CSweetPlatformMemoryStore` (Broker package) proxies every `IMemoryStore` call over the SDK's `platform.memory.{query,write,manage,export}.v1` capabilities.

**Platform side** (`csweet`). `PlatformMemoryCapabilityHandler` deserializes the command, resolves the installation's employee identity, checks tenant match and (when the partition carries an `AgentId`) employee match, and calls the shared Postgres store. `AgentMemoryService` is a *second*, independent recall/capture implementation used by the chat gateway: `RecallForConversationAsync` (own query splitting, own ranking, own rendering, 6,000-char budget), `CaptureMessageAsync` (episode per message, enrichment of user turn paired with assistant turn), `ProcessPendingAsync` driven by `MemoryCaptureWorker` (limit 1, pauses while any chat turn is active). `ChatPromptPolicy` wraps the recalled text in `<memory_context>` inside the user prompt.

**Agent side.** `VideoGameCreativeDirectorAgent` news up a `MemoryEngine` over `CSweetPlatformMemoryStore` with `DelegatedMemoryScopeAuthorizer`, no enrichment queue, no query embedder, and (because it bypasses DI) the default `PrimaryMemoryNamespaceResolver`. It recalls twice per pitch (user-relationship namespace, 800 tokens; organization namespace, 1,200 tokens) with fixed descriptive queries, and proposes explicit JSON episodes for user preferences and project decisions.

## 2. Retrieval quality — evidence

**2.1 Vector channel is dead in production.** `MicrosoftExtensionsAIMemoryEnricher` only embeds if given an `IEmbeddingGenerator`; `AgentMemoryService.EnrichWithTelemetryAsync` constructs it with the chat client only. `MemoryEngine` only embeds the query if an `IMemoryQueryEmbedder` is registered; the Creative Director registers none and the platform's recall path never calls `MemoryEngine`. Even if embeddings existed, both stores implement "vector search" as `SELECT all embeddings in partition` + cosine in C#, which does not scale past a few thousand episodes per namespace. Postgres has no `pgvector` column or index.

**2.2 Semantic / graph / procedural channels cannot match natural language.** All three use `LIKE '%<query>%'` (ILIKE on Postgres) with the whole query string against `predicate`, `value`, `canonical_name`, or the procedure `name`/JSON payload. A query like "manager involvement, interaction style, milestone review, collaboration, and creative approval preferences" will never be a substring of a claim value. These channels only work for single-token queries, which is exactly why `AgentMemoryService.BuildSearchQueries` splits into up to six ≥3-char non-stopword terms. Through `MemoryEngine` there is no such split, so the Creative Director's recalls of claims, edges and procedures return nothing.

**2.3 Postgres full-text ANDs all terms.** `plainto_tsquery('simple', @query)` joins terms with `&`. With the `simple` configuration there is no stemming and no stop-word removal, so "What did we decide about the platform?" requires an episode containing *what, did, we, decide, about, the, platform*. SQLite's `ToFtsQuery` ORs quoted words instead — so the two stores have opposite recall characteristics, and tests (SQLite only) do not exercise the production behaviour.

**2.4 Fusion has little to fuse.** RRF is correctly implemented and tested, but in practice one channel (episodic full-text) supplies nearly all candidates, so ranking collapses to bm25/ts_rank order. There is no recency decay, no importance/confidence contribution for episodes (episodic candidates get synthetic scores 1.0, 0.98, …), no usage feedback, and no per-layer quota, so a budget can be consumed entirely by old transcript lines.

**2.5 Entity resolution is exact-name only.** `UpsertEntityAsync` merges on `(partition, lower(canonical_name))` or application key. "Matt", "Matt Wood" and "the CEO" are three entities; claims about them never join and the graph walk never connects them. The alias fallback in `FindEntityAsync` deserializes every entity in the partition per lookup.

**2.6 Contradictions accumulate on the platform path.** `MemoryEnrichmentWorker` supersedes conflicting claims; the platform's own `AgentMemoryService.EnrichEpisodeAsync` (a copy of that logic) does not. Since production enrichment runs only through the platform path, "preferred engine = Unity" and "preferred engine = Godot" will both be live and both recalled. The worker's version also does `ListClaimsAsync` for the whole partition once per extracted claim, which is O(claims × extractions) per episode.

**2.7 Enrichment is memory-blind.** The extractor sees one episode and nothing else, so it cannot decide ADD vs UPDATE vs NOOP, cannot reuse existing entity names, and re-extracts the same facts every time a user restates them. There is no structured-output mode, no few-shot, and no guidance toward the operational references (`MemoryOperationalReference`) that the model is designed around.

**2.8 Throughput.** `MemoryCaptureWorker` processes one message per 10–15 s and stops whenever any chat turn is active anywhere in the organization. Under even light interactive use the enrichment backlog grows and semantic memory lags conversations by minutes to hours.

## 3. Context injection and bloat — evidence

**3.1 Raw transcript is the main payload.** Every user and assistant message is an episode. Episodic full-text is the only reliable channel. Therefore most of what is injected is prior chat lines, including the assistant's own previous answers (which are already covered by `<recent_conversation>` in the same prompt — the chat prompt carries both the recent transcript and memory, so recent turns can appear twice).

**3.2 Budgets exist but are per-call and stack.** Platform chat: top 8 items, each truncated to 1,200 chars, total ≤ 6,000 chars (~1,500 tokens). Engine: default 2,000 estimated tokens per `RecallAsync`. The Creative Director runs two recalls (800 + 1,200) and embeds both rendered contexts inside a JSON grounding object that is itself a user message; when the agent is reached via chat, the platform has already injected up to 6,000 chars of memory into that same message. Realistic worst case is ~3,500–4,000 tokens of memory per turn, mostly transcript.

**3.3 Oversized items are silently dropped, not trimmed.** `MemoryEngine` skips any candidate whose estimated tokens exceed the remaining budget and keeps going. An applied knowledge transfer is rendered as *one* episode containing every transferred item (budget 8,000 tokens by default); at a 2,000-token recall budget it can never fit, so a replacement employee's transferred knowledge is unrecallable through the engine until something enriches it into claims — and nothing does, because platform enrichment only processes conversation messages. The same applies to the Creative Director's explicit JSON proposals: they are stored, FTS-searchable, never enriched, and injected as raw JSON when they match.

**3.4 Token estimation is chars/4 with no tokenizer**, and the platform path budgets in characters. Fine as a guard, but neither path reports actual tokens, so bloat is invisible in telemetry. The SDK's `CountMemoryCharacters` looks for the exact tag `<memory_context>`, so engine-rendered contexts (`<memory_context trust="untrusted">`) and anything embedded in JSON are not counted.

**3.5 Prompt-injection hygiene is good on the engine path only.** `MemoryEngine.Render` escapes `<`/`>` and labels the block untrusted; the chat system prompt tells the model to treat memory as supporting context only; the extractor prompt says not to treat quoted instructions as authoritative; and `IsDurableMessage` stops re-ingesting injected memory. Keep all of this. But the platform path does not get it: `AgentMemoryService.RecallForConversationAsync` appends candidate content verbatim, and `ChatPromptPolicy` wraps that inside `<memory_context>` delimiters, so a remembered message containing `</memory_context><current_user_message>…` is not neutralised. The same defensive serialization is needed there.

## 4. Governance and security — evidence

**4.1 Redaction is client-side and self-asserted.** `SafeMemoryRedactor` runs inside `MemoryEngine`, i.e., inside the agent process, and reads `memory.maxSensitivity` from the principal the agent constructs for itself (the Creative Director sets `Personal`). The broker's `search` operation applies only tenant/employee partition checks and returns full `Content` for every candidate regardless of sensitivity. Any installed agent can therefore read `Confidential`/`Restricted` memory in namespaces it can reach (organization, team, role, case — none carry an `AgentId`) by setting a higher attribute or using `PassthroughMemoryRedactor`.

**4.2 Authorization is coarse at the broker.** `Authorize` enforces same tenant and, only when the partition has an `AgentId`, same employee. The `MemoryAction` (Read/Propose/Manage) is discarded ("access level is represented by the explicit capability grant"). Any agent with `write` can write to the organization namespace; any agent with `manage` can `delete-scope` any non-employee partition in the tenant. The rich `IMemoryScopeAuthorizer` abstraction exists but the only production implementation (`DelegatedMemoryScopeAuthorizer`) is tenant-equality.

**4.3 Confirmation workflows have no operator surface in the code I read.** Procedures are `Pending` until confirmed and are never recalled while pending; sensitive claims likewise. I found no caller of `SetClaimConfirmationAsync`/`ConfirmClaimAsync` in `AgentMemoryService` or the chat API, and no UI contract for it in `AgentMemoryContracts`. If that's true in the UI too, procedural memory is permanently empty.

**4.4 Knowledge transfer is well designed** (approval-gated, filtered by sensitivity/layer/budget, excludes raw episodes by default, provenance-preserving) and well tested. Its only weakness is the delivery format noted in 3.3.

## 5. Where this sits against the state of the art

The field converged in 2025–26 on a few patterns; CSweet.Memory has the *schema* for all of them but the *runtime* for few.

Mem0's approach is extract-then-reconcile: the LLM sees new facts *and* the top-k existing memories and emits ADD/UPDATE/DELETE/NOOP decisions, which keeps the store small and current; Mem0 reports large token reductions versus full-context on LoCoMo precisely because the store holds consolidated facts, not transcript. CSweet's extractor is memory-blind (2.7) and stores transcript as the primary recall unit (3.1).

Zep/Graphiti is the closest architectural sibling: bi-temporal edges with invalidation, hybrid retrieval (BM25 + cosine + graph neighbourhood) fused with RRF, entity resolution via embedding similarity plus LLM dedupe, and a "community"/summary layer for coarse context. CSweet matches the temporal graph and RRF on paper but is missing working vectors, entity resolution, and any summary layer, and the Postgres FTS semantics undercut even the lexical channel.

Letta/MemGPT contributes the always-in-context core blocks that the agent itself edits (persona, user, task) with a hard size cap, plus paged archival recall. CSweet has `MemoryBlock` with `MaximumTokens` and `IsPinned`, and the Core layer ranks first — but nothing writes blocks.

Benchmarks the community uses (LoCoMo, LongMemEval, Zep's DMR) all reward consolidated, time-aware facts over raw retrieval, and all measure *tokens injected per correct answer* as well as accuracy. CSweet has no evaluation harness; its tests prove authorization and plumbing, not retrieval quality.

Sources for the landscape: [Mem0 — State of AI agent memory 2026](https://mem0.ai/blog/state-of-ai-agent-memory-2026), [Mem0 — LoCoMo benchmark](https://mem0.ai/blog/locomo-benchmark), [Mem0 — Memory evaluation](https://docs.mem0.ai/core-concepts/memory-evaluation), [Atlan — Mem0, Zep, LangChain, Letta compared](https://atlan.com/know/best-ai-agent-memory-frameworks-2026/), [ecorpit — Mem0 vs Zep vs Letta vs Cloudflare](https://ecorpit.com/ai-agent-memory-mem0-zep-letta-cloudflare-comparison-2026/), [Developers Digest — memory providers 2026](https://www.developersdigest.tech/blog/best-ai-agent-memory-providers-2026), [Codebridge — AI memory frameworks](https://www.codebridge.tech/articles/best-ai-memory-frameworks).

Verdict on "are we using the best approach": the chosen approach (own temporal property graph over Postgres, governance-first, MAF integration) is the right one for a multi-tenant agentic business and I would not swap it for Mem0/Zep as a dependency. That is a judgment about fit, not a measured comparison. The gap is execution, and it is closable incrementally.

## 6. Recommendations (original ordering — superseded by §8.4)

The list below is kept for reference; the review in §8 re-prioritises it so that namespace alignment, server-side policy enforcement and replay-safe enrichment come before embeddings and automatic summaries, and amends several of the proposed fixes.

**P0 — make retrieval actually work in production**

1. Fix Postgres full-text: use `websearch_to_tsquery` or build an OR'd `to_tsquery` from extracted terms, switch the `tsvector` to `english` (stemming + stop words), and add a per-episode `occurred_at` recency factor to the rank. Add a Postgres integration test (Testcontainers) that mirrors the SQLite search tests so the two stores stop diverging.
2. Replace `LIKE '%query%'` for claims/edges/procedures with term-level matching: build a searchable text per claim (`subject predicate value`) and index it with FTS/tsvector, or at minimum apply the `BuildSearchQueries` term split inside the stores so `MemoryEngine` callers get the same behaviour the platform path does.
3. Turn on embeddings end-to-end: register an `IEmbeddingGenerator` in `AgentMemoryService.EnrichWithTelemetryAsync`, embed *claims and procedures* (not just episodes), add `pgvector` with an HNSW index to `csweet_memory_embeddings`, and give the broker a `search` that passes the query embedding through. Until pgvector lands, cap the in-process scan (most recent N) so recall latency is bounded.
4. Move redaction server-side: have `PlatformMemoryCapabilityHandler` redact candidates above the installation's *granted* sensitivity ceiling before returning them, and honour `MemoryAction` (read/propose/manage) per namespace type instead of discarding it. Treat `memory.maxSensitivity` from agents as a request, not a grant.

**P1 — reduce bloat and raise value per token**

5. Stop treating transcript as the recall unit. Keep episodes as the immutable audit log, but make the default recall layers `Core + Semantic + Procedural`, with episodic lines only as evidence when a claim is selected (or when the agent explicitly asks for history). Exclude assistant-authored episodes from default recall entirely; they are already in `<recent_conversation>`.
6. Add per-layer quotas and recency decay to `ReciprocalRankFusion`, and trim oversized candidates to fit instead of skipping them (3.3). Report actual token counts in the `MemoryContextPacket` and in `csweet.memory.*` metrics; fix the SDK tag match so engine-rendered contexts are counted.
7. Unify the two recall paths. `AgentMemoryService.RecallForConversationAsync` should call `MemoryEngine.RecallAsync` with the `WorkContextMemoryNamespaceResolver` rather than re-implementing ranking and rendering, so fixes land once. Coordinate budgets so platform injection and agent-side recall don't stack (pass the already-recalled memory ids/budget through the agent event context).
8. Write Core blocks. Add a small nightly (or N-turns) consolidation job per employee and per user-relationship that produces/updates two or three pinned blocks (e.g., `employee.operating-profile`, `relationship.preferences`, `project.current-state`) under a hard `MaximumTokens`. This is the single highest-leverage change for "operate within the business long term" — it gives every turn a stable, cheap, always-on summary and lets episodic retrieval be smaller.

**P1 — close the learning loop**

9. Make enrichment memory-aware: retrieve top-k existing claims/entities for the episode's namespace and include them in the extraction prompt; have the model emit `add | update(supersedes id) | noop`. Use structured output (JSON schema) rather than fence stripping. Port the worker's conflict/supersession logic into the platform path (or, better, have the platform path *use* `MemoryEnrichmentWorker`), and replace the per-claim `ListClaimsAsync` with a subject+predicate lookup (the index already exists).
10. Enrich everything, not just conversation messages: agent proposals, applied knowledge transfers, and future work-item events need a path into the outbox. Store knowledge-transfer items as individual claims/procedures in the target namespace rather than one monolithic episode.
11. Use `MemoryUse`. Have the chat gateway record `Cited` when a response contains a `memory:` citation and let the agent SDK record `Accepted`/`Corrected`; feed a usage prior into RRF. Build the confirmation surface for pending claims and procedures (manager-facing), otherwise procedural memory stays empty.
12. Entity resolution: embedding-similarity + LLM confirm on upsert for `learned:` entities, and always attach `applicationKey` for platform-known Person/Role/Task/Goal entities so they merge deterministically.

**P2 — extend memory to the workforce that does the work**

13. The Producer, Software Developer, QA and Technical Director agents have no memory. The highest-value memories for them are not chat: sprint retros, ticket outcomes, blockers and how they were resolved, code-review feedback, and repository conventions. Define a small set of *typed* ingest events in the SDK (`WorkOutcome`, `Failure`, `Handoff`, `Decision`) that `CSweetManagerAgentBase`/worker bases emit at ticket transitions, routed to employee, team and case namespaces. This uses the `MemoryClaimKind` taxonomy that already exists and avoids transcript bloat by construction.
14. Give the enrichment worker real throughput: process in batches, don't pause globally on any interactive turn (pause per-organization or lower priority instead), and run it in `CSweet.WorkerHost` rather than alongside the API.

**P2 — measure**

15. Add a retrieval evaluation harness: a few dozen seeded conversations per namespace type with gold "should recall" facts, scoring recall@k, tokens injected, and contradiction rate, run in CI against both stores. Without this, every change above is a guess.

## 7. Things to keep

Immutable episodes with checksums and idempotency; bi-temporal claims and `AsOf` queries; trust/confirmation/sensitivity on every record; deny-by-default engine authorization and tenant/employee checks at the broker; the org-shaped namespace model and `WorkContextMemoryNamespaceResolver`; RRF as the fusion method; the escaped, untrusted-labelled rendering and the no-reingest guard; the approval-gated knowledge transfer; OpenTelemetry on ingest/recall; fail-open with timeouts and turn-trace events in the chat worker. These are the parts that are better than the off-the-shelf options.

## 8. Review revisions (2026-10-06)

A second review of the same code agreed with the §2–§4 findings, pushed back on the priority order, and added five findings. I re-checked each against the source; all five hold. This section records them and the resulting plan, which supersedes §6.

### 8.1 The two memory paths do not share namespaces

`AgentMemoryService` builds every namespace with `ApplicationId = "csweet"`. The Creative Director calls `EmployeeMemoryNamespaces.UserRelationship(context.BusinessId, context.Identity.EmployeeId, userId, context.InstallationId)` and `Organization(context.BusinessId, context.InstallationId)`, passing the *installation id* into the application segment. Both resolve the same employee id (`AgentMemoryIdentityResolver` maps the installation to `AgentOrganizationUser.Id`, which is what the platform uses), so the only difference is that segment — but it is enough to make the partition keys differ. The relationship and organization memories written by chat and by the agent therefore live in sibling partitions that never meet. Search improvements alone will not fix this. Needed: a canonical namespace contract (what is business-shared vs installation-private), construction of that contract in one place (ideally server-side from the session identity, not from agent-supplied fields), and a migration of existing records.

### 8.2 Policy enforcement gaps go beyond search redaction

In addition to the client-side redaction issue in §4.1: the broker returns raw content from `get-claim`, `list-claims`, `export` and `get-knowledge-transfer`; `write-claim`, `write-edge`, `write-procedure` and `append-episode` accept whatever `Trust`, `Confirmation` and `Sensitivity` the agent supplies (so an agent can mint `Authoritative`/`Confirmed` claims); and `write-knowledge-transfer` accepts the package `Status`, so the approval gate can be skipped by writing an already-`Approved` package. Separately, `MemoryPartition.Key` drops empty segments and joins the rest with an unescaped `/`, so `new MemoryPartition("t","csweet", UserId:"emp2", CustomNamespace:"employee:emp2")` produces the same key as the real employee partition `new MemoryPartition("t","csweet", AgentId:"emp2", CustomNamespace:"employee:emp2")` while carrying a null `AgentId` — which is exactly the field `PlatformMemoryCapabilityHandler.Authorize` checks. Needed: server-constructed or server-validated canonical scopes (fixed-arity key with escaped separators, or a hash), membership and sensitivity enforced on every operation, trust elevation/confirmation/transfer transitions owned by the server, and adversarial tests for key collisions and forged metadata.

### 8.3 Rejected claims and unredacted content on the platform path

The claim query in both stores excludes only `confirmation <> 1` (Pending); `Rejected` (3) rows are returned and relied on `ReciprocalRankFusion` to zero them out. `AgentMemoryService.RecallForConversationAsync` does its own selection and never calls RRF or `IMemoryRedactor`, so rejected claims and above-ceiling sensitive content can reach the chat prompt. Confirmation, authorization, sensitivity and temporal eligibility are correctness filters and belong in the store/server query, not in ranking.

### 8.4 Enrichment is not replay-safe

The platform enricher derives deterministic ids (`SHA256(episodeId:kind:key)`) for claims, edges and procedures, but `PostgreSqlMemoryStore.InsertAsync` is a plain `INSERT` with no `ON CONFLICT` (only blocks upsert). A cancellation after partial writes — which `MemoryCaptureWorker` induces deliberately whenever a chat turn starts — leaves the outbox item `Pending`, and the retry collides on primary key and fails again, until the item hits ten attempts and is marked `Failed`. The pending query (`Status != Completed && NextAttemptAt <= now`) also re-selects `Failed` and `Processing` rows with no lease, so a second worker instance would double-process. Needed before any throughput work: idempotent writes (`ON CONFLICT DO NOTHING`/`DO UPDATE` keyed on the deterministic id), reconciliation as one transaction per episode, expiring leases on outbox items, and an explicit terminal state.

### 8.5 Derived memory loses sensitivity

`MemoryEdge`, `ProceduralMemory` and `MemoryBlock` have no sensitivity field and are surfaced as `Internal` by both stores' `SearchAsync`. A procedure or edge extracted from a `Restricted` episode is therefore recallable by anyone who clears the `Internal` bar. Any consolidation/summary layer (the Core blocks proposed in §6.8) would have the same problem unless derived records inherit the maximum sensitivity of their sources and carry source links so they can be refreshed on correction or deletion.

### 8.6 Amendments to proposed fixes

- `websearch_to_tsquery` is not a fix for all-terms matching: unquoted words are still `&`-joined. Build the tsquery deliberately — OR'd extracted terms with phrase/entity boosts and a broader fallback — and choose the text-search configuration on purpose, since `english` stemming will mangle identifiers and product names that matter here (`Godot`, ticket keys, repo names). Consider a `simple` + `english` dual index or an unaccented `simple` with explicit synonym handling.
- Do not swap the platform's durable outbox for `MemoryEnrichmentWorker`'s in-memory channel. Extract the reconciliation logic into a reusable service and call it from the outbox processor.
- Do not port the worker's supersession rule unchanged. "Same subject + predicate, different value" is not always a contradiction (an employee can use both Unity and Godot). Reconciliation needs predicate cardinality, scope (project/case), evidence authority and event time, and should consider all live conflicting claims, not `FirstOrDefault`.
- Prefer evidence-preserving chunking over trimming oversized candidates; truncating a procedure can drop a prerequisite, truncating a decision can drop its qualifier.
- Keep a bounded episodic fallback until structured memory is dependable. Exclude already-supplied transcript by message id rather than excluding all assistant-authored episodes.
- Treat `Cited` as usage evidence, not validation; a repeatedly cited wrong memory must not be promoted.
- Core blocks remain the highest-leverage long-term change, but they should land after eligibility enforcement and invalidation work: blocks need source links, inherited sensitivity (§8.5), revisions, and refresh on correction/deletion. Canonical task and project state should keep coming from operational records, not from summaries.
- For embeddings, record provider/model identity and dimensions per embedding, plan backfill and re-embedding, and test *filtered* vector recall: pgvector approximate indexes can under-return after a partition/sensitivity filter, so an HNSW index alone is not sufficient.

### 8.7 Revised delivery order

| Priority | Work | Acceptance evidence |
|---|---|---|
| P0 | Canonical namespaces + server-enforced memory policy (§8.1, §8.2, §8.3, §4.1) | Chat and agents read/write the same intended partitions; unauthorized scopes, forged trust/confirmation/status, key collisions, rejected claims and over-ceiling or derived-sensitive content cannot reach a prompt |
| P0 | PostgreSQL regression tests + retrieval baseline (§2.3, §2.2) | Representative natural-language questions retrieve the expected facts on Postgres; temporal and expiry rules hold across all channels; both stores behave the same |
| P0 | Durable, replay-safe enrichment (§8.4) | Restart, cancellation, duplicate delivery and concurrent workers yield one consistent result; no duplicate-key failures |
| P1 | One recall pipeline + one invocation-wide budget (§3.1–3.4, §6.7) | Shared eligibility/ranking/rendering/escaping; recent-history deduplicated by message id; rendered memory fully accounted in telemetry |
| P1 | Reconciliation + operator controls (§2.6, §2.7, §4.3) | Users can inspect provenance, correct facts, confirm procedures and claims, and see why a memory was supplied |
| P2 | Typed work outcomes for delivery agents, embeddings, compact Core summaries (§6.13, §6.3, §6.8) | Measurable gains in work quality and recall-per-token with no isolation or latency regressions |

First implementation slice: namespace alignment, eligibility/security enforcement at the broker, Postgres idempotent writes, and a small evaluation suite that reports answer correctness, recall@k, stale/contradictory results, injected tokens, latency and enrichment age. That baseline is what tells us whether embeddings and summaries are worth their cost.

## Appendix — specific code references

- `MemoryEngine.RecallAsync` budget skip: `if (tokens > budget - usedTokens) continue;`
- `MemoryEngine` constructor defaults: `namespaceResolver ?? new PrimaryMemoryNamespaceResolver()`; Creative Director bypasses DI, so cross-namespace fan-out is manual.
- `SqliteMemoryStore.SearchAsync` / `PostgreSqlMemoryStore.SearchAsync`: `LIKE $query` with `%{request.Query}%` for semantic, graph roots and procedures; `plainto_tsquery('simple', @query)` on Postgres; vector = full partition scan.
- `AgentMemoryService.EnrichWithTelemetryAsync`: `new MicrosoftExtensionsAIMemoryEnricher(capturingClient)` — no embedding generator.
- `AgentMemoryService.EnrichEpisodeAsync`: writes claims without conflict detection or `SupersedeClaimAsync`.
- `AgentMemoryService.RecallForConversationAsync`: 3 namespaces × (1 + ≤6) queries × all layers; top 8; 1,200 chars/item; 6,000 chars total.
- `PlatformMemoryCapabilityHandler.Authorize`: tenant equality + optional `AgentId` equality; `_ = action;`.
- `SafeMemoryRedactor`: reads `memory.maxSensitivity` from the agent-supplied principal.
- `VideoGameCreativeDirectorAgent.CreateMemoryEngine`: no enrichment queue, no embedder, `DelegatedMemoryScopeAuthorizer`; `CreateMemoryAccess` sets `memory.maxSensitivity = Personal`.
- `MemoryCaptureWorker`: `ProcessPendingAsync(limit: 1)`, pauses while any `ChatTurn` is active.
- No callers of `WriteBlockAsync` or `RecordFeedbackAsync` outside the store/handler; no callers of `SetClaimConfirmationAsync` in `AgentMemoryService` or the chat API.
- `PlatformChatClient.CountMemoryCharacters`: matches `<memory_context>` exactly; engine renders `<memory_context trust="untrusted">`.
- `AgentMemoryService` line 26: `ApplicationId = "csweet"`; `VideoGameCreativeDirectorAgent` lines 1161–1173: `context.InstallationId` passed as the application segment.
- `MemoryModels.cs` `MemoryPartition.Key`: `string.Join('/', …Where(not blank))` — variable arity, unescaped separator.
- `PlatformMemoryCapabilityHandler.HandleWriteAsync`: passes agent-supplied `MemoryClaim.Trust/Confirmation`, `KnowledgeTransferPackage.Status` straight to the store.
- `PostgreSqlMemoryStore.InsertAsync`: plain `INSERT`, no `ON CONFLICT`; `AgentMemoryService.DeterministicId`: SHA-256 of `episodeId:kind:key`.
- `AgentMemoryService.ProcessPendingAsync`: `Status != Completed && NextAttemptAt <= now`, no lease.
- Both stores' claim query: `confirmation <> 1` only — `Rejected` rows returned; `AgentMemoryService` never calls `ReciprocalRankFusion` or `IMemoryRedactor`.
- Both stores' `SearchAsync`: edges, procedures and blocks emitted with `MemorySensitivity.Internal` hardcoded.
