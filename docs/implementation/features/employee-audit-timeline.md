# Employee audit timeline

The **Timeline** tab in `EmployeeDetails` is an employee-scoped view of the existing security audit ledger. It does not maintain a second event history. `SecurityAuditService` supplies both the Security page and employee timeline; `AuditEventInspector` displays the same event IDs and payloads in both places.

## Capture and integrity

### Chief of Staff work cards

`ChiefWorkCards` and `WorkItemMutationEngine.ChiefAudit` create a platform-managed
personal-board card before dispatching work for an active Chief of Staff. `AgentWorkInbox`
prepares the card during enqueue and binds it to `AgentWorkAttempt` and `WorkExecutionContext`
inside the claim transaction before returning the payload. All callback paths, including
inherited manager, coordination, onboarding, attention and capability handlers, share this
boundary. `ChatTurnWorker.ProcessAsync` prepares the same conversation-turn card before
runtime preparation or memory recall. Repeated delivery and chat-turn attempts reuse the card;
previous claim/binding receipts and inference ownership remain intact.

`McpGatewayEndpoints.CurrentCapabilityAuditContextAsync` requires a single authenticated
Chief delivery and a usable card before a tool executes. Chief callbacks are serialized by
the inbox and the Chief manifest's `maximumConcurrentJobs`. `PlatformLlmCapabilityHandler`
also validates the card at provider dispatch and uses `ChiefWorkCards.BindInferenceAsync`
to capture the exact runtime attempt when optional model telemetry lacks a work hint. Chief
inference receipt persistence failures propagate; they cannot silently permit unrecorded dispatch.
Existing capability, policy, memory and execution checks still supply authority.

`ChiefOfStaffAgent.OnActivatedAsync` performs no platform actions. Startup/reconnect recovery
runs in `HandleManagerAttentionReviewAsync` through the existing durable attention scheduler,
after the callback's card is bound. `AgentMemoryService.EnrichWithTelemetryAsync` creates
and links a separate memory card and initial receipt before each background enrichment call.
`HiringService.SubmitDelegatedAsync` creates an automatic-hiring card before candidate selection;
`WorkflowSnapshot.AuditWorkItemId` retains its identity through deferred builds. Operation
changes update the card and append `chief.hiring.operation` activities and board outbox hints
with the operation mutation. Multi-seat fulfillment keeps the card active until all seats are
filled; missing authority/setup or failures leave it blocked.

These tracking cards have `IsExecutable = false`, so their creation does not enqueue another
personal callback. Existing executable commitments may become the callback's focus while they
run; the callback card resumes accounting for subsequent tools after that commitment finishes.
Completion, failure, cancellation and lease-loss recovery retain card and attempt history.
Provider-unreported token counts remain unknown. No historical tokens or missing actions are
invented. Deploy the matching platform and Chief 2.13.4 through Installed Agents; no migration,
new capability grants or installation-record patching is required.

Coverage: `AgentWorkInboxTests.ChiefCards`, `HiringServiceAutonomyTests.Leadership`,
`AgentMemoryServiceTests.RelationshipMemory_IsRecalledAcrossConversationsAndBrowsable`, and
Chief `ChiefOfStaffProfileTests.StartupDoesNotPerformUnleasedActions`.

### Shared audit capture

- `CSweetDbContext.CaptureAgentAuditEvents` observes messages, participant changes, chat turns/traces, internal work and attempts/progress, board work/activity/orchestration, model run logs, runtime events, and targeted platform-event state changes. It queues evidence in the same save/transaction as the source mutation. Lease renewal bookkeeping does not emit another work-start event.
- `CSweetDbContext.QueueAudit` uses the shared `AuditOutbox`. The existing physical `ComputeAuditOutbox` table and entity name remain for migration compatibility; compute and agent producers use the same dispatcher. New evidence is sanitized and protected at rest when the runtime data-protection provider is present. Preserve the application's data-protection keys with database backups.
- `AuditOutboxDispatcher` retries each record with bounded backoff; a failed record remains pending and does not prevent other records being delivered. Stable event IDs prevent duplicate ledger appends after a lost acknowledgement. `AuditOutboxWorker` also performs bounded historical discovery; agents do not poll for audit delivery.
- `AuditEventWriter` appends one canonical event, its employee associations, a protected full payload, and a realtime outbox hint together. Integrity v2 seals the association JSON and full sanitized payload hash in addition to the v1 evidence. Existing v1 records retain their original seals and remain verifiable.
- `PlatformLlmCapabilityHandler` captures effective request instructions and readable response chunks, including tool content and supplied reasoning artifacts, with their sequence. Existing telemetry failure behavior remains: a persistence failure is logged and does not abort inference. Such a failure can leave a capture gap.
- MCP capability calls record a start and correlated terminal result, including arguments and results. A process loss before the terminal record leaves the start as evidence of an incomplete invocation.
- Message receipt means availability to a participant at that moment, not proof that the agent processed it. The same message record appears as sent for its actor and received for its recipients. Work and tool records describe processing separately.

`AuditPayloadSanitizer` supplies the shared redaction policy for the ledger and chat traces. Full retained evidence is available separately from the bounded preview. Credentials, restricted memory fields, and protected/encrypted model reasoning remain redacted. No new model-generated summaries are substituted for recorded content. Provider-private reasoning and operations never reported through platform boundaries cannot be reconstructed.

## Query and access

`SecurityEventSummaryResponse.ActivityHref` links Activity text to its recorded context. `SecurityAuditService.ResolveActivityLinksAsync` resolves canonical work items (including personal-board item links) and conversations using bounded, organization-scoped source reads. `CSweetDbContext.DescribeAuditSource` captures `activityWorkItemId`, `activityConversationId`, and `activityPerspectiveEmployeeId` in audit metadata alongside the source transaction. MCP starts capture the authenticated attempt's current work focus; terminal receipts inherit that start's context. Legacy records use recorded source values or explicit execution/source relationships, never the employee's current assignment. Sent messages and chat-sending tools open `/communications/{chatId}?perspective={sendingAgentEmployeeId}`; destination APIs retain their existing perspective and work-item authorization. Missing destinations or unresolved agent identity leave Activity as plain text.

`SecurityEventSummaryResponse.ActivityDescription` supplies the Security page's short Activity label. `AuditActivityDescriptions.Describe` maps common events and legacy capability receipts to readable descriptions without changing the original `Summary`, wire keys, or integrity-sealed evidence. `AuditActionDescriptions` owns short tool labels separately from model-facing tool instructions; unknown tool names have a readable fallback. `McpToolDescriptor.ActivityDescription` is captured as `activityDescription` in both start and terminal audit metadata so future records retain their label. Add event descriptions in `AuditActivityDescriptions` and action descriptions in `AuditActionDescriptions` when introducing new operations. Technical event/category keys and the original summary remain in `AuditEventInspector`; the Activity column shows only the description and, for grouped model responses, the combined part count.

`EmployeeEndpoints` exposes `GET /api/core/organizations/{organizationId}/employees/{employeeId}/timeline` and `GET .../timeline/{eventId}`. List filters are `from`, `to`, `category`, `outcome`, `search`, and exact `correlationId`, with opaque `cursor` and `limit` (default 50, maximum 200). Timeline pages sort by occurrence time then ledger sequence, descending. The Security page retains its sequence ordering and individual audit records.

`SecurityEventQuery.GroupModelResponses` is enabled for the employee timeline. `SecurityAuditService.BrowseAsync` groups `model.response.chunk` and the terminal `model.call.completed` receipt by organization, employee scope, and `AgentRunLog` entity ID before filtering/pagination. The latest receipt represents one **Model response** row; separate model calls in the same correlated turn stay separate. The row includes a chunk count and the terminal outcome when recorded.

`SecurityAuditService.AssembleModelResponseAsync` reads the original protected chunk evidence at a stable ledger watermark, orders it by stream sequence, concatenates text without duplicating text content parts, and retains tool/reasoning content and latest token usage. It checks each receipt and payload. `ModelResponseEvidence` displays the assembled response and expandable source links; no synthetic event or parallel history is persisted. Inspection is bounded to 10,000 chunks and 2 million payload characters, with an explicit incomplete indicator for limits, missing, or damaged evidence; the first 100 source links are listed. All original chunks remain in the shared audit log. Metadata-only streaming updates have no response text; newly captured chunks include role, finish reason, and additional usage to explain them.

`EmployeeAuditAccess` permits active human organization owners and human reporting ancestors of the active agent. It fails closed for hierarchy cycles. This is explicit diagnostic access to private interaction evidence; normal chat endpoints still enforce conversation membership. Detail reads are themselves audited. The UI shows audit-page links only to roles already permitted to use that page.

`ApplicationRealtimeOutboxDispatcher` resolves timeline hint recipients from the current organization hierarchy at delivery time. Hints contain employee IDs only. `EmployeeTimeline` rechecks REST on hints and reconnect, offers a refresh control without moving the reader's scroll position, and uses inline details on narrow screens. The tab loads only when selected.

## Historical data and retention

Reviewed memory erasure recognizes content-free activity metadata through
`MemoryAuditAssociations.Matches`. The three fields `activityWorkItemId`,
`activityConversationId` and `activityPerspectiveEmployeeId` must exactly match the retained
chat turn, trace owner or model run. Legacy null metadata remains supported. Unknown fields,
duplicate/missing fields, malformed values and links to another primary record require review;
they cannot be treated as safely omitted memory content. `AgentMemoryReviewService` and
`MemoryWorkErasure` apply the same check to pending outbox evidence and delivered audit rows.
`MemoryAuditAssociationTests` covers strict link validation and PostgreSQL erasure rejection.

The Memory contracts workflow partitions its existing selection into memory-erasure,
other-memory and runtime/model suites. The final `memory-contracts` check requires every suite
to pass; each suite publishes its own test-result artifact.

`AuditHistoryImporter` imports bounded batches into the same ledger. Outbox source receipts checkpoint discovery and make restart safe. It reuses existing records rather than replacing sealed evidence. Legacy association indexes use explicitly recorded actor IDs and message recipient metadata; installation-only historical identity is not guessed from today's employee links.

Mutable historical records are labelled `audit.source.snapshot`. They cannot recover previous message versions, missing work transitions, full model requests omitted by older logging, or absent reasoning artifacts. Available immutable traces/attempts retain their recorded evidence and timestamps. The UI identifies preview-only or missing payloads.

All ledger records and full diagnostic payloads are retained under the existing audit behavior. **90 days is only the default timeline viewing window.** Choose **All history** to inspect older retained events. There is no timeline-specific deletion job.

## Deployment and verification

Apply the `EmployeeAuditTimeline` migration through the normal C-Sweet migrator and restart the API, AgentHost, and web app. The migration adds evidence/association tables, lookup indexes, and delivery metadata without deleting or resealing existing audit records. Historical import and outbox delivery run after startup; large histories populate incrementally.

Coverage lives in `EmployeeAuditTimelineTests`, `EmployeeAuditPostgresTests`, `EmployeeTimelineRenderingTests`, and the existing audit, communications, MCP, model, and work-inbox suites. The PostgreSQL test opts in through `CSWEET_COMPUTE_TEST_DATABASE`, creates a uniquely named `audit_test_*` database, applies the migration chain, verifies rollback and query behavior, and removes only its test database.
