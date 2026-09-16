# Employee audit timeline

The **Timeline** tab in `EmployeeDetails` is an employee-scoped view of the existing security audit ledger. It does not maintain a second event history. `SecurityAuditService` supplies both the Security page and employee timeline; `AuditEventInspector` displays the same event IDs and payloads in both places.

## Capture and integrity

- `CSweetDbContext.CaptureAgentAuditEvents` observes messages, participant changes, chat turns/traces, internal work and attempts/progress, board work/activity/orchestration, model run logs, runtime events, and targeted platform-event state changes. It queues evidence in the same save/transaction as the source mutation. Lease renewal bookkeeping does not emit another work-start event.
- `CSweetDbContext.QueueAudit` uses the shared `AuditOutbox`. The existing physical `ComputeAuditOutbox` table and entity name remain for migration compatibility; compute and agent producers use the same dispatcher. New evidence is sanitized and protected at rest when the runtime data-protection provider is present. Preserve the application's data-protection keys with database backups.
- `AuditOutboxDispatcher` retries each record with bounded backoff; a failed record remains pending and does not prevent other records being delivered. Stable event IDs prevent duplicate ledger appends after a lost acknowledgement. `AuditOutboxWorker` also performs bounded historical discovery; agents do not poll for audit delivery.
- `AuditEventWriter` appends one canonical event, its employee associations, a protected full payload, and a realtime outbox hint together. Integrity v2 seals the association JSON and full sanitized payload hash in addition to the v1 evidence. Existing v1 records retain their original seals and remain verifiable.
- `PlatformLlmCapabilityHandler` captures effective request instructions and readable response chunks, including tool content and supplied reasoning artifacts, with their sequence. Existing telemetry failure behavior remains: a persistence failure is logged and does not abort inference. Such a failure can leave a capture gap.
- MCP capability calls record a start and correlated terminal result, including arguments and results. A process loss before the terminal record leaves the start as evidence of an incomplete invocation.
- Message receipt means availability to a participant at that moment, not proof that the agent processed it. The same message record appears as sent for its actor and received for its recipients. Work and tool records describe processing separately.

`AuditPayloadSanitizer` supplies the shared redaction policy for the ledger and chat traces. Full retained evidence is available separately from the bounded preview. Credentials, restricted memory fields, and protected/encrypted model reasoning remain redacted. No new model-generated summaries are substituted for recorded content. Provider-private reasoning and operations never reported through platform boundaries cannot be reconstructed.

## Query and access

`EmployeeEndpoints` exposes `GET /api/core/organizations/{organizationId}/employees/{employeeId}/timeline` and `GET .../timeline/{eventId}`. List filters are `from`, `to`, `category`, `outcome`, `search`, and exact `correlationId`, with opaque `cursor` and `limit` (default 50, maximum 200). Timeline pages sort by occurrence time then ledger sequence, descending. The Security page retains its sequence ordering and individual audit records.

`SecurityEventQuery.GroupModelResponses` is enabled for the employee timeline. `SecurityAuditService.BrowseAsync` groups `model.response.chunk` and the terminal `model.call.completed` receipt by organization, employee scope, and `AgentRunLog` entity ID before filtering/pagination. The latest receipt represents one **Model response** row; separate model calls in the same correlated turn stay separate. The row includes a chunk count and the terminal outcome when recorded.

`SecurityAuditService.AssembleModelResponseAsync` reads the original protected chunk evidence at a stable ledger watermark, orders it by stream sequence, concatenates text without duplicating text content parts, and retains tool/reasoning content and latest token usage. It checks each receipt and payload. `ModelResponseEvidence` displays the assembled response and expandable source links; no synthetic event or parallel history is persisted. Inspection is bounded to 10,000 chunks and 2 million payload characters, with an explicit incomplete indicator for limits, missing, or damaged evidence; the first 100 source links are listed. All original chunks remain in the shared audit log. Metadata-only streaming updates have no response text; newly captured chunks include role, finish reason, and additional usage to explain them.

`EmployeeAuditAccess` permits active human organization owners and human reporting ancestors of the active agent. It fails closed for hierarchy cycles. This is explicit diagnostic access to private interaction evidence; normal chat endpoints still enforce conversation membership. Detail reads are themselves audited. The UI shows audit-page links only to roles already permitted to use that page.

`ApplicationRealtimeOutboxDispatcher` resolves timeline hint recipients from the current organization hierarchy at delivery time. Hints contain employee IDs only. `EmployeeTimeline` rechecks REST on hints and reconnect, offers a refresh control without moving the reader's scroll position, and uses inline details on narrow screens. The tab loads only when selected.

## Historical data and retention

`AuditHistoryImporter` imports bounded batches into the same ledger. Outbox source receipts checkpoint discovery and make restart safe. It reuses existing records rather than replacing sealed evidence. Legacy association indexes use explicitly recorded actor IDs and message recipient metadata; installation-only historical identity is not guessed from today's employee links.

Mutable historical records are labelled `audit.source.snapshot`. They cannot recover previous message versions, missing work transitions, full model requests omitted by older logging, or absent reasoning artifacts. Available immutable traces/attempts retain their recorded evidence and timestamps. The UI identifies preview-only or missing payloads.

All ledger records and full diagnostic payloads are retained under the existing audit behavior. **90 days is only the default timeline viewing window.** Choose **All history** to inspect older retained events. There is no timeline-specific deletion job.

## Deployment and verification

Apply the `EmployeeAuditTimeline` migration through the normal C-Sweet migrator and restart the API, AgentHost, and web app. The migration adds evidence/association tables, lookup indexes, and delivery metadata without deleting or resealing existing audit records. Historical import and outbox delivery run after startup; large histories populate incrementally.

Coverage lives in `EmployeeAuditTimelineTests`, `EmployeeAuditPostgresTests`, `EmployeeTimelineRenderingTests`, and the existing audit, communications, MCP, model, and work-inbox suites. The PostgreSQL test opts in through `CSWEET_COMPUTE_TEST_DATABASE`, creates a uniquely named `audit_test_*` database, applies the migration chain, verifies rollback and query behavior, and removes only its test database.
