# CSweet.Memory: verified assessment and implementation plan

Date: 2026-10-06  
Status: Finalized plan; ready to begin the P0 implementation slice. Implementation and release gates remain outstanding.  
Scope: CSweet.Memory, C-Sweet platform integration, agent SDK memory access and telemetry, and the Video Game Creative Director integration.

This document consolidates the original evaluation and both subsequent reviews. It replaces their recommendation lists with one implementation backlog. There is no separate superseded delivery order to follow.

## 1. Decision and evidence

Retain the current .NET memory library and PostgreSQL architecture for this work. Preserve immutable source episodes, provenance, explicit namespaces, confirmation and trust concepts, temporal validity, and bounded retrieval. Strengthen the runtime and integration before adding embeddings, automatic summaries, or widespread agent adoption.

The first delivery must establish that memory is isolated correctly, reaches the intended employee, survives retries, and can retrieve a small set of known facts. Retrieval quality improvements must be measured against that baseline.

### Evidence status

- The findings below come from source inspection across the repositories listed in Section 10. They describe the inspected implementation, not a sampled production deployment.
- The existing CSweet.Memory test suite passed all 20 tests during the preceding review. It does not cover PostgreSQL or the identified cross-repository integration failures. This result is a baseline, not acceptance of the planned changes.
- No live production recall rate, typical context composition, throughput limit, or enrichment backlog was measured. Claims that retrieval usually fails or that backlog takes hours remain hypotheses until instrumented.
- No comparative benchmark against commercial memory products was performed. Retaining the architecture is a decision about product fit and scope, not a claim of superiority.
- Record the repository commit IDs and relevant package versions when implementation starts so test results and migration evidence can be tied to an exact revision.

## 2. Architecture and product boundaries

### Current components

| Component | Responsibility and current behavior |
|---|---|
| `CSweet.Memory.Abstractions` | Episodes, claims, entities, edges, procedures, core blocks, namespaces, access context, usage records, and knowledge-transfer contracts. |
| `MemoryEngine` | Authorization hooks, ingest, namespace fan-out, optional query embedding, rank fusion, redaction, token estimation, rendering, correction, confirmation, and transfer orchestration. |
| `MemoryEnrichmentWorker` | Optional enrichment through a bounded in-memory channel. Writes extracted records and performs a limited conflict check. |
| `MicrosoftExtensionsAIMemoryEnricher` | Extracts structured data from an episode using a chat client; optionally generates an embedding. |
| `SqliteMemoryStore` / `PostgreSqlMemoryStore` | Persist records and implement lexical, graph, and optional vector retrieval. Their current search and write behaviors differ. |
| `CSweetPlatformMemoryStore` | Proxies store-shaped operations through SDK platform memory capabilities. |
| `PlatformMemoryCapabilityHandler` | Resolves installation identity, checks capability grants and selected partition fields, then calls the shared store. |
| `AgentMemoryService` / `MemoryCaptureWorker` | Separate platform chat recall, capture, enrichment, inspection, and outbox processing. |
| `ChatPromptPolicy` / `PlatformChatClient` | Place recalled memory in prompts and account for selected prompt content. |
| `VideoGameCreativeDirectorAgent` | Explicitly recalls relationship and organization memory and proposes selected preferences and decisions. |

### Boundaries to preserve

Operational platform records remain authoritative for employee identity, membership, reporting relationships, assignments, tasks, approvals, and current project state. Memory records observations and experience about those records using stable operational references. An extracted statement cannot grant access, change an assignment, or approve work.

Memory policy is enforced by trusted platform services before content is returned or sent to a model. Agent-side checks and redaction provide additional protection but cannot grant authority.

Source episodes remain evidence. Derived records are revisable interpretations with explicit provenance, sensitivity, validity, and confirmation. The presence of `RecordedAt` and `ValidFrom`/`ValidTo` does not by itself establish complete bitemporal history; supported historical-query semantics must be specified and tested.

## 3. Findings to address

### F1. Platform and agent namespaces diverge

`AgentMemoryService` uses `ApplicationId = "csweet"`. The Creative Director passes its installation ID into the application segment of `EmployeeMemoryNamespaces.UserRelationship` and `Organization`. These produce different partition keys for otherwise corresponding memories. Improving search cannot bridge that separation.

Define one namespace contract that explicitly distinguishes organization-shared, employee, relationship, team, role, case, and installation-private knowledge. Ownership must follow authoritative platform identities, with installation identity retained separately where needed.

### F2. Policy enforcement is incomplete at the trusted boundary

`PlatformMemoryCapabilityHandler` checks tenant equality and an optional employee field. It does not implement the complete namespace-specific membership, sensitivity, and action policy described by the model. Search, claim reads, lists, exports, and transfer reads can return raw content. Write operations accept records carrying agent-supplied trust, confirmation, sensitivity, or transfer status rather than enforcing every transition server-side.

`MemoryPartition.Key` drops blank fields and joins the remaining values with unescaped separators. Distinct field assignments can resolve to the same stored key while passing different field-based authorization checks.

Record references also require validation: current SQL joins resolve entities and episodes by ID without consistently including partition equality. Authorizing a new record's declared partition does not establish that its referenced records belong there.

### F3. Eligibility and sensitivity differ between recall paths

Store claim queries exclude pending claims by default but can return rejected claims. Engine rank fusion removes rejected candidates; platform recall uses a separate selection path and does not apply that protection or the engine redactor.

Edges, procedures, and blocks lack explicit sensitivity fields and are surfaced as `Internal`. Extracted claims use the extractor's sensitivity rather than an enforced source-derived minimum. Applied transfers omit an explicit ingest sensitivity and can therefore use the `Internal` default. Sensitive source material can lose restrictions as it becomes derived memory.

Temporal eligibility also needs cross-channel tests. For example, the inspected PostgreSQL vector query does not apply the lexical channel's expiry filter, and graph traversal does not consistently enforce `ValidFrom`.

### F4. Enrichment is not safely replayable

Platform enrichment derives deterministic IDs from episode and extracted content. PostgreSQL claim, edge, and procedure writes use plain inserts. Cancellation after partial writes can cause duplicate-key failures on replay.

`ProcessPendingAsync` selects all non-completed statuses, including failed or processing records, without an ownership lease. Cancellation can leave a processing item eligible for subsequent selection. Multiple workers can process the same item, and a nominally failed item can be selected again.

Deterministic IDs alone are insufficient: another model call can produce different extracted content and therefore different IDs for the same source episode. The library worker's in-memory channel is not a substitute for durable platform work.

### F5. Retrieval is restrictive and inconsistent

- PostgreSQL episodic retrieval uses `plainto_tsquery('simple', ...)`, requiring all surviving terms. SQLite builds an OR query.
- Claim, graph-root, and procedure searches use whole-query substring matching, which is poorly suited to long descriptive queries.
- Platform chat compensates with up to seven queries across three namespaces. The Creative Director's engine recalls do not use that query splitting.
- Embeddings are not connected end to end in the inspected platform paths. Existing vector retrieval scans partition embeddings in process and joins them to episodes.
- Semantic and procedural candidate queries need deliberate ordering before limits, so relevant candidates are not discarded before fusion.
- Exact names, application keys, and an alias scan support entity lookup; there is no broader entity reconciliation pipeline.

These behaviors explain likely retrieval weaknesses, but their frequency and user impact require measurement.

### F6. Context assembly and accounting are duplicated

Platform chat and the engine select, rank, render, and budget memory independently. Platform chat can select eight items, truncate each to 1,200 characters, and cap the combined memory text at 6,000 characters. The Creative Director separately requests 800 and 1,200 estimated tokens. These allowances can accumulate.

Recent conversation and recalled episodes are not comprehensively deduplicated by source message ID. Engine packing skips candidates that do not fit. A large transfer episode can therefore be omitted even when relevant. Memory-specific accounting is estimated or character-based, and SDK tag detection misses the engine's attributed opening tag.

Engine rendering escapes delimiters. Platform recall appends candidate content directly before `ChatPromptPolicy` wraps it in `<memory_context>`. The platform path needs equivalent defensive serialization. Escaping is useful hygiene, not a guarantee against all model-level prompt injection.

### F7. Reconciliation and operator workflows are incomplete

The extractor sees the current episode without existing memory. Platform enrichment writes claims without reconciling previous values. The library worker's alternate rule treats the first different value for a subject/predicate as a conflict, which is unsafe for multivalued or differently scoped facts.

Extracted procedures are pending, and the inspected platform UI/API provides inspection without a complete confirmation/correction workflow. Usage capture does not close the loop between supplied context and explicit user validation. Proposals and transfers lack the conversation outbox's enrichment path.

### F8. Lifecycle and workforce coverage need explicit contracts

The capture worker pauses for active chat turns across its database query and cancels enrichment when interactive work starts. That can delay enrichment; the degree of delay is not measured.

Scope deletion removes selected memory tables but omits transfer packages. Pending jobs and retained source messages can recreate derived knowledge unless deletion and backfill semantics prevent it. Legal-hold and expiry fields require enforced lifecycle behavior, not just storage.

The inspected Producer, Software Developer, QA, and Technical Director implementations lack explicit long-term memory integration for delivery work. Platform chat recall does not establish that their autonomous work execution learns from outcomes.

## 4. Required implementation invariants

These apply across all delivery phases and are release requirements.

1. **Canonical identity:** the server resolves namespace ownership and current access from authoritative identity and membership. Caller-supplied keys, principal attributes, source types, and metadata cannot elevate privileges.
2. **Referential isolation:** every claim, edge, embedding, correction, and transfer reference must resolve to an allowed record and scope. Use same-partition constraints for local relationships. Any deliberately supported cross-scope provenance link requires explicit authorization rather than unrestricted joins.
3. **Eligibility before use:** recall excludes rejected, unauthorized, expired, and temporally ineligible records before packing. Pending records appear only in explicitly authorized review operations. Inspection/export privileges are distinct from ordinary recall privileges.
4. **Inherited restrictions:** derived memory retains source links and at least the maximum applicable source sensitivity. Membership restrictions also survive derivation; sensitivity alone does not authorize a broader audience. Declassification is an explicit, audited privileged action.
5. **Durable ingestion:** accepting an ingest and recording the durable work required to process it occur atomically. Generalize this to agent proposals, transfers, and later work outcomes. Events are wake hints; authorized current-state reads and bounded recovery remain available.
6. **Replay-safe effects:** accepted extraction output is versioned and persisted for replay. Applying it checks record revisions and the worker's current lease token. Duplicate delivery yields one consistent result; different content under the same idempotency identity produces a visible conflict.
7. **Bounded execution:** model calls occur outside database transactions. Use short transactions for reconciliation and state changes, bounded query work, retry limits, worker leases, and per-provider/per-tenant resource controls.
8. **Safe degradation:** unavailable memory may allow a conversation to continue without recalled context. Failed policy evaluation releases no protected content. Revalidate permissions at execution and before externally visible effects, including queued enrichment and exports.
9. **Controlled model input:** policy applies before memory is sent to enrichment, embedding, or response providers. Diagnostic content follows the same sensitivity and access rules. Keep routine metrics content-free.
10. **Durable forgetting:** deletion or exclusion prevents queued work and backfills from silently recreating the same memory. Legal holds govern retention, while recall suppression and access rules remain explicit.

## 5. Consolidated implementation backlog

### P0-A. Canonical namespaces, server policy, and privacy-preserving migration

**Primary repositories:** CSweet.Memory, csweet, CSweet.Agent.Sdk, CSweet.Agent.CreativeDirector.VideoGame.

Implement a shared namespace contract with server construction or strict canonical validation. Use fixed-field, unambiguous encoding or a hash of canonical structured identity; hashing an already ambiguous string is insufficient. Treat the logical employee identity independently from an installation's lifecycle.

Enforce read, propose, manage, confirmation, export, and transfer permissions on every applicable endpoint and capability. Enforce current organization/team/role/case/relationship access. Own trust elevation, source attribution, confirmation changes, and transfer state transitions on the server. Validate referenced records and add appropriate database constraints.

Add source-linked sensitivity and audience restrictions to derived types and transfers. Apply shared eligibility checks to platform chat immediately; do not wait for P1 pipeline unification to fix leakage. Apply safe rendering on both paths in this phase.

Migration must distinguish shared from private legacy namespaces. Do not automatically promote installation-private content into organization scope. Use a reviewable mapping and conservative defaults for records with unknown provenance or sensitivity. Quarantine ambiguous collisions and restrict them until ownership can be established.

Specify migration order, checkpoints, backup/recovery, supported old/new package combinations, and rollback. Old clients must be safely adapted or explicitly rejected; they must not recreate ambiguous partitions. Preserve record IDs and provenance where feasible, and migrate indexed columns and serialized payloads consistently.

**Acceptance evidence:**

- Platform chat and the Creative Director read the same intended employee/relationship/business memories; private namespaces remain private.
- Tests reject key collisions, forged metadata, unauthorized membership, scope changes, and foreign referenced IDs across every exposed operation.
- Rejected and over-ceiling records, including derived and transferred content, cannot enter ordinary recall or unauthorized provider requests.
- Permission revocation between enqueue and execution is honored.
- Populated-database migration tests cover both existing application segments, ambiguous keys, mixed sensitivity, interrupted migration, supported old clients, and recovery.
- No migration or rollback broadens access to compensate for missing metadata.

### P0-B. PostgreSQL correctness tests and retrieval baseline

**Primary repositories:** CSweet.Memory and csweet test projects.

Run real PostgreSQL integration tests in CI using an isolated test instance. Share store contract fixtures with SQLite while allowing implementation-specific ranking. Include end-to-end tests through the broker and platform recall, not only direct store calls.

Improve lexical retrieval with deliberate term construction, phrase/entity boosts, and bounded fallback. Index searchable claim/procedure text that includes useful subject and applicability information. Avoid multiplying unbounded per-term database calls. Choose language handling deliberately and preserve identifiers, ticket keys, repository names, and supported languages.

`websearch_to_tsquery` alone does not solve the all-terms issue: unquoted terms are still AND-connected. A text-search configuration change requires a database migration for stored vectors/indexes, not merely an edited initialization string.

Define time semantics for each channel. Test validity boundaries, expiry, supersession, late-arriving events, and historical requests. Document whether `AsOf` represents valid time; do not claim historical knowledge-as-recorded unless that separate behavior exists.

Seed a fixed set of representative employee and business scenarios. Maintain required facts, irrelevant distractors, forbidden records, expected abstentions, and historical answers. Keep deterministic retrieval/policy tests separate from model-dependent answer evaluation.

**Acceptance evidence:**

- Expected facts are retrieved for designated natural-language questions on PostgreSQL and SQLite.
- Policy and temporal fixtures produce zero forbidden results across lexical, graph, vector fallback, and core/procedure channels where supported.
- Identifiers and names remain searchable; short, long, empty, and punctuation-heavy queries are bounded and handled deliberately.
- Store-specific ranking differences are allowed if both meet the shared retrieval and eligibility requirements.
- Baseline reports include retrieval quality, context size, query count, latency, and corpus size. Performance thresholds are agreed from measurements before release rather than invented during planning.

### P0-C. Durable enrichment and lifecycle correctness

**Primary repositories:** CSweet.Memory and csweet.

Extract reusable enrichment/reconciliation logic without replacing the platform outbox with the library worker's in-memory channel. Provide a durable processing path for all accepted ingestion sources, including agent proposals and approved transfers.

Use an explicit state machine with pending, leased processing, completed, and terminal-failure behavior. Acquire work atomically; use expiry and a lease token/version to prevent an obsolete worker from committing. Add bounded retries and deliberate operator retry for terminal failures.

Persist validated extraction output and its extractor/model/schema version before applying it. Retries replay that accepted output. Intentional re-extraction uses an explicit new version and reconciliation policy. A crash before output persistence may repeat inference, but must not create duplicate applied effects.

Perform extraction outside transactions, then commit derived changes and reconciliation state in a short transaction. Where source, memory, and job state cannot share one database transaction, use a durable idempotent handoff and recovery record; document that boundary explicitly. Never rely on distributed timing or an in-memory notification for correctness.

Add conflict-aware idempotent writes. Protect against partial writes, concurrent corrections, and revision changes while extraction is running. Apply transfers idempotently and retain source restrictions and item-level provenance.

Define deletion, suppression, expiry, and legal-hold behavior for episodes, derivatives, embeddings, transfer packages and applied copies, usage history, and any caches. Specify which transferred knowledge remains independently valid and which must be invalidated. Outstanding jobs must check durable suppression/version state before writing. Retained source conversations must not be silently backfilled after a user has asked to forget them.

After correctness is established, improve throughput using bounded batches, fair tenant scheduling, and provider-aware concurrency. Preserve interactive capacity without globally starving enrichment. Moving execution to WorkerHost is an operational option; it is not a replacement for safe worker ownership.

**Acceptance evidence:**

- Crash/cancellation tests at each persistence boundary recover without lost accepted work, duplicate effects, or duplicate-key retry loops.
- Two workers competing for the same item produce one accepted result; a worker with an expired lease cannot commit.
- Changed LLM output on a retry cannot silently create another set of applied facts.
- Failed items stay terminal until explicitly retried; permanent failure is visible to operators.
- Deletion, expiry, and suppression during extraction prevent prohibited material from returning through retries or backfills.
- Transfer replay does not duplicate knowledge or downgrade sensitivity.
- Sustained interactive traffic leaves bounded background progress, measured with enrichment-age and queue metrics.

### P1-A. One recall pipeline and one invocation budget

**Primary repositories:** CSweet.Memory, csweet, CSweet.Agent.Sdk, and the Creative Director agent.

Route platform and agent recalls through common policy, query planning, ranking, context selection, and rendering. Keep definitive checks at the trusted server boundary. Reuse `MemoryEngine` components where appropriate, but preserve platform observability and fail-open behavior for availability.

Coordinate one invocation-wide context allowance. Pass structured context identifiers, selected source IDs, and consumed/remaining budget so platform and agent calls do not stack independent allowances. Deduplicate recent conversation by source message ID and memory evidence, not only text equality.

Prefer useful claims, confirmed procedures, and later curated blocks while retaining bounded episodic fallback. Keep full source evidence available on demand. Split oversized material into evidence-preserving units; preserve prerequisites and qualifiers rather than blindly truncating procedures or decisions.

Count the final rendered context, including citations, delimiters, escaping, and serialization overhead. Distinguish estimates, tokenizer measurements, and provider-reported total usage. Do not label unavailable per-memory actual token usage as measured. Replace tag-only accounting with structured attribution where possible.

**Acceptance evidence:**

- Platform and agent paths share eligibility, rendering, and ranking behavior for the same authorized request.
- A combined platform-plus-agent invocation respects its configured memory allowance and does not duplicate recent transcript items.
- Long procedures and transfers retain meaningful, provenance-linked units within the budget.
- Telemetry identifies supplied items and rendered size accurately; items excluded by packing are not reported as supplied.
- Memory or telemetry failures degrade visibly without releasing protected content or unnecessarily preventing an otherwise valid response.

### P1-B. Memory-aware reconciliation and operator controls

**Primary repositories:** CSweet.Memory, csweet contracts/API/UI, and SDK where needed.

Retrieve a bounded, authorized set of relevant existing facts and entities for extraction. Validate structured output before application, with a bounded compatibility path for providers without native structured-output support. Treat proposed add/update/no-op decisions as untrusted proposals; validate IDs, provenance, permissions, predicate rules, and revisions in deterministic code.

Define predicate cardinality and scope. Distinguish coexistence from replacement, and consider event time and evidence authority. An inference must not silently supersede a confirmed fact. Evaluate all relevant live claims, not the first differing value.

Use authoritative application keys for known operational entities. Maintain indexed aliases. Keep uncertain entity merges reviewable and reversible; similarly named employees must not be merged solely by model judgment.

Extend the existing memory inspection UI with source evidence, current/historical state, correction, rejection, procedure confirmation, and reasons for retrieval. Provide appropriately authorized forget/suppress controls with the lifecycle semantics from P0-C. Record reviewer identity and revision checks for conflicting edits.

Record supplied, cited, accepted, corrected, and rejected outcomes with clear meanings. Validate that cited IDs were supplied to the relevant invocation. Citation frequency is usage evidence, not a promotion to confirmed truth.

**Acceptance evidence:**

- Repeated statements reconcile without uncontrolled growth; legitimate multivalued facts coexist.
- Corrections and late events preserve defined historical truth and cannot silently override stronger evidence.
- Operators can confirm or reject a procedure and observe the intended recall change.
- Every displayed memory and correction has inspectable provenance and an attributable state transition.
- Feedback cannot forge citations, elevate trust, or bypass authorization.

### P2-A. Typed work outcomes for delivery agents

**Primary repositories:** SDK, csweet, and selected delivery-agent repositories.

Introduce typed, provenance-bearing events for decisions, work outcomes, failures, handoffs, and feedback. Prefer authoritative persisted work transitions where available. Include stable event IDs and task/project/repository or artifact versions so repeated delivery is idempotent and lessons retain their applicability.

Route each event to the minimum intended employee/team/case audience under server policy. Integrate recall into one delivery workflow first, then expand based on evaluation. Capture useful resolution and review evidence rather than every intermediate agent message.

**Acceptance evidence:** a repeated delivery scenario demonstrates useful recall of a prior decision or failure resolution, correct audience isolation, and a measured improvement in work quality without excessive context growth.

### P2-B. Embeddings and scalable semantic retrieval

**Primary repositories:** CSweet.Memory, csweet provider configuration and deployment infrastructure.

Connect authorized embedding generation, persistence, query embedding, and search end to end. Record provider/model identity, dimensions, and version; matching dimensions alone does not make vectors from different models compatible. Plan backfill, model replacement, and re-embedding.

Evaluate embeddings for compact claims and procedures as well as evidence chunks. Implement bounded PostgreSQL vector retrieval with an appropriate index and tenant/scope/sensitivity/temporal filtering. Verify filtered recall: approximate indexing can return fewer eligible results after filtering. Preserve a measured lexical fallback and record degraded behavior when providers or vector support are unavailable.

**Acceptance evidence:** a controlled comparison against P0 lexical retrieval shows an agreed quality improvement at acceptable latency, token, storage, and inference cost, including restrictive tenant/sensitivity filters and a model-version migration.

### P2-C. Compact core summaries

**Primary repositories:** CSweet.Memory and csweet.

Create a small number of bounded, source-linked blocks for stable operating preferences, relationship context, or project lessons. Preserve sensitivity and audience restrictions, revisions, explicit confirmation where required, and source invalidation. Read current operational state from its authoritative service instead of copying it into a stale summary.

Trigger refresh from durable changes with debouncing and bounded recovery. Add a scheduled safety pass only where needed; do not require per-agent polling loops. Do not recursively treat previous model summaries as independent evidence.

**Acceptance evidence:** summaries reduce injected context while preserving answer quality; correction, source deletion, and access revocation invalidate or restrict affected blocks before subsequent use.

## 6. Delivery sequence and release gates

### First implementation slice

1. Capture revisions and write focused failing policy, namespace, replay, and PostgreSQL retrieval fixtures.
2. Implement the canonical namespace/policy contract and its migration design.
3. Close current eligibility, rendering, referential-isolation, and sensitivity gaps.
4. Implement durable, conflict-aware writes and worker ownership, including accepted extraction replay and lifecycle checks.
5. Deliver the bounded lexical baseline and run the populated-data migration rehearsal.
6. Release only when the P0 gates below pass. Proceed to P1, then evaluate P2 additions individually.

Tests and implementation may be developed together. A passing library suite alone does not authorize declaring P0 complete.

### Executable gates

| Gate | Required evidence |
|---|---|
| Isolation and policy | Zero unauthorized results in the fixed adversarial suite across recall, inspection, export, writes, transfers, referenced IDs, provider dispatch, and permission revocation. |
| Eligibility | Rejected, pending-for-ordinary-recall, expired, future-invalid, and out-of-scope records are excluded under the defined semantics across supported channels. |
| Durability | Fault injection and concurrent-worker tests demonstrate one accepted effect, recoverable interruption, fenced stale workers, and explicit terminal failure. |
| Migration | Fresh install and populated upgrade both pass; legacy privacy is preserved; interruption and rollback/recovery are rehearsed against supported versions. |
| Retrieval | Shared golden fixtures retrieve required evidence and abstain where appropriate on both stores. Identical raw scores or ranking order are not required. |
| Quality and performance | Publish before/after measurements and agreed thresholds for the changed behavior. Improvements must not trade away isolation, correctness, or bounded execution. |
| Packaging | Changed packages are built, tested, packed, and inspected; downstream consumers pass using the intended package versions with sibling references disabled where applicable. |

The evaluation report must separate deterministic retrieval results from model-dependent answer correctness. Record dataset version, model/provider, corpus size, and test configuration. Include recall@k, irrelevant/stale/contradictory result rates, correct abstentions, injected context size, query counts, p50/p95 latency, enrichment age, retries/terminal failures, and inference cost where available. Keep a held-out scenario set for judging retrieval changes.

## 7. Release and compatibility requirements

- Apply semantic versioning to changed public contracts and packages. Update downstream pins and documented/template/test versions together.
- For SDK changes, follow the repository's SDK version-bump requirement. The inspected checkout is `CSweet.Agent.Sdk`; contributor instructions also mention the legacy `CSweetAgentSdk` path. Resolve the actual authoritative project path before editing or packing, and never skip the required bump because of that naming difference.
- If WorkManagement.Contracts or Office.Contracts changes, follow their explicit cross-repository version and consumer-verification requirements.
- For an agent repository, bump and synchronize its version before writing release notes. Read the final version from `csweet-plugin.json`; match the release-note filename and heading. Preserve published historical notes.
- Build/test and pack every changed package; inspect the resulting `.nupkg` identity, version, and dependencies. Verify consumers against the intended packaged artifacts with sibling references disabled, rather than relying on local project references to hide stale pins.
- Document database migrations, capability/protocol compatibility, deployment order, operator recovery, and the oldest supported client version. Recovery must never restore a policy bypass.
- Keep new documentation symbol-first. Update the implementation-plan index when the finalized plan is adopted into the maintained documentation set.

## 8. Operational visibility and product experience

Expose whether memory is healthy, catching up, unavailable, or awaiting operator action using actual processing state and age. A terminally failed item should not appear indefinitely as ordinary catch-up work.

Users should be able to inspect why a memory was supplied, its source, its audience, whether it is inferred or confirmed, and how to correct or suppress it. Avoid presenting internal extraction or database terminology in routine chat unless it helps the user make a decision.

Use content-free telemetry by default. Diagnostic evidence containing memory text requires explicit access and retention rules. Record degradation and policy denial without leaking the denied content through error messages.

## 9. External technical references

These references explain implementation mechanics; they do not establish comparative product quality.

- [PostgreSQL: Controlling Text Search](https://www.postgresql.org/docs/current/textsearch-controls.html) — query construction, configuration, and ranking; ordinary `websearch_to_tsquery` terms remain AND-connected.
- [pgvector: Filtering](https://github.com/pgvector/pgvector#filtering) — filtered approximate retrieval, index considerations, and iterative scanning.

Recheck version-specific behavior against the versions selected for implementation. Vendor benchmark claims from earlier drafts are not release evidence for C-Sweet.

## 10. Symbol-first code map

Paths below are relative to the named repository. Confirm locations against the implementation revision before editing.

| Repository | File | Relevant symbols |
|---|---|---|
| CSweet.Memory | `src/CSweet.Memory.Abstractions/MemoryModels.cs` | `MemoryPartition.Key`, `MemoryEpisode`, `MemoryClaim`, `MemoryEdge`, `ProceduralMemory`, `MemoryBlock`, `KnowledgeTransferPackage` |
| CSweet.Memory | `src/CSweet.Memory.Abstractions/MemoryContracts.cs` | `IMemoryStore`, `IMemoryEngine`, `IMemoryEnrichmentQueue`, `IMemoryScopeAuthorizer`, `MemorySearchRequest` |
| CSweet.Memory | `src/CSweet.Memory/MemoryEngine.cs` | `IngestAsync`, `RecallAsync`, `CorrectClaimAsync`, `ConfirmClaimAsync`, `PrepareKnowledgeTransferAsync`, `ApproveKnowledgeTransferAsync`, `ApplyKnowledgeTransferAsync` |
| CSweet.Memory | `src/CSweet.Memory/MemoryEnrichmentWorker.cs` | `EnqueueAsync`, `ExecuteAsync`, `ProcessAsync` |
| CSweet.Memory | `src/CSweet.Memory/MicrosoftExtensionsAIMemoryEnricher.cs` | `EnrichAsync`, `EmbedAsync`, extraction contract |
| CSweet.Memory | `src/CSweet.Memory/AgentMemoryOptions.cs` | `SafeMemoryRedactor`, `DelegatedMemoryScopeAuthorizer`, `EmployeeMemoryNamespaces`, `WorkContextMemoryNamespaceResolver` |
| CSweet.Memory | `src/CSweet.Memory/ReciprocalRankFusion.cs` | `Rank` |
| CSweet.Memory | `src/CSweet.Memory.PostgreSql/PostgreSqlMemoryStore.cs` | `SearchAsync`, `InsertAsync`, `UpsertEntityAsync`, `DeleteScopeAsync`, `Schema`, transfer persistence |
| CSweet.Memory | `src/CSweet.Memory.Sqlite/SqliteMemoryStore.cs` | `SearchAsync`, `ToFtsQuery`, persistence and lifecycle equivalents |
| CSweet.Memory | `src/CSweet.Memory.Broker/CSweetPlatformMemoryStore.cs` | `CSweetMemoryCapabilities`, store-to-broker operations |
| CSweet.Memory | `tests/CSweet.Memory.Tests/` | Existing store, employee-memory, rank-fusion, and framework integration tests |
| csweet | `src/CSweet.AgentHost/Broker/PlatformMemoryCapabilityHandler.cs` | `Authorize`, query/write/manage/export handlers |
| csweet | `src/CSweet.AgentHost/Broker/AgentMemoryIdentityResolver.cs` | `ResolveAsync` |
| csweet | `src/CSweet.Infrastructure/Core/AgentMemoryService.cs` | `RecallForConversationAsync`, `CaptureMessageAsync`, `ProcessPendingAsync`, `EnrichEpisodeAsync`, `EnrichWithTelemetryAsync`, `DeterministicId`, inspection methods |
| csweet | `src/CSweet.Infrastructure/Core/MemoryCaptureWorker.cs` | `ExecuteAsync`, interactive cancellation |
| csweet | `src/CSweet.Infrastructure/Core/ConversationService.cs` | Message persistence and `MemoryCaptureOutbox` creation |
| csweet | `src/CSweet.Domain/Core/MemoryCaptureOutboxItem.cs` | `MemoryCaptureStatus`, processing state |
| csweet | `src/CSweet.Api/Chat/ChatPromptPolicy.cs` | `BuildConversationPrompt` |
| csweet | `src/CSweet.Api/Core/AgentMemoryEndpoints.cs` | Inspection endpoints and hierarchy access |
| csweet | `src/CSweet.Contracts/Memory/AgentMemoryContracts.cs` | Inspection and future operator-action contracts |
| csweet | `src/CSweet.UI/Pages/AgentMemory.razor` | Memory inspection surface |
| csweet | `tests/CSweet.UnitTests/AgentMemoryServiceTests.cs` | Platform recall/inspection coverage |
| CSweet.Agent.Sdk | `src/CSweet.Agent.SDK/PlatformChatClient.cs` | `CountMemoryCharacters`, prompt accounting |
| CSweet.Agent.CreativeDirector.VideoGame | `src/CSweet.Agent.CreativeDirector.VideoGame/VideoGameCreativeDirectorAgent.cs` | `RecallApprovedMemoryAsync`, `BuildExplicitMemoryProposals`, `CreateMemoryEngine`, `CreateMemoryAccess` |

