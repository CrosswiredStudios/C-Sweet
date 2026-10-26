# Generic plugin account lifecycle and administration

Status: implemented increment with asynchronous cleanup confirmation, not an onboarding readiness gate. This is not a declaration of complete
YouTube policy compliance or production acceptance. See `plugin-account-management-progress.md`.

## Code map

- `PluginAccountService.Operators`: administrator-owned human operator assignments, current
  package/profile binding, scoped reconnect and cancellation after revocation.
- `PluginAccountService.Management`: impact-hash/CAS reviewed pause, resume and logical retirement.
- `PluginAccountService.Lifecycle`: database-scheduled discovery and metadata expiry. Maintenance
  has no fabricated human user; it cannot initiate consent or change the verified account identity.
- `PluginAccountService.Disconnect` / `.Deletion`: immediate account fencing, provider revocation,
  repeatable primary cleanup and independent confirmations.
- `CSweetDbContext.ProviderData`: platform-owned content-field classification and atomic provenance.
- `PluginAccountDataExposureService`: provenance propagation after authorized native JSON responses.
- `PluginRuntimeDataDeletionParticipant`: destruction confirmed through `IAgentWorkloadRunner`.
- `PluginRetainedEvidenceDeletionParticipant`: dedicated PostgreSQL scope, exact recorded work
  closure, existing `MemoryWorkErasure`, and fail-closed audit-content verification.
- `PluginAuditRetentionGuard`: checks pending/published immutable evidence; never edits the ledger.
- `PluginSignedCleanupParticipant`, `PluginCleanupTransport`, `PluginCleanupOptions`: deployment
  mTLS destinations and exact-scope, signed external cleanup confirmations.
- `PluginAdministrationPanel`, `PluginUpdatePanel`, `PluginManage`: native controls; plugin labels,
  scopes, resources and policy links come from reviewed declarations, never executable plugin UI.

No Google/YouTube package IDs, channel enums, provider scopes or provider request rules were added
to these host services. OAuth and bootstrap discovery continue to use the approved manifest.

## Administration UX

Install and approve the package once for the enterprise, then land on its account page. An empty
installation is valid. Connect account remains the primary action. Build diagnostics/updates and
operator assignments are expandable administration sections rather than technical onboarding fields.

An administrator can assign a particular account operator, or explicitly acknowledge all accounts
and new connections. Assignment approves the actual matching deployment profile for this package,
not an arbitrary profile ID. Existing human accounts are selected; nobody receives credentials.
Operator authority is independent from business resource grants and each consuming agent's binding.

Pause/resume/removal requires a current impact hash and administration revision. Pausing stops
external work, invalidates pending plans and disables standing policies. Resume does not restore
autonomy. Removing initiates all account disconnects; the installation remains visibly pending until
cleanup confirms, then becomes Retired. Historical content-free receipts remain accessible.

`IPluginInstallationService.ListAsync` excludes retired revisions from Installed while direct
`GetAsync` reads remain available for historical receipts. `AgentInstallationResponse.RemovalPending`
projects the persisted removal request so `Plugins` can display **Removal in progress**, not Disabled,
while connected-account cleanup is outstanding. The page also filters retired responses from older
servers before computing installed counts, catalog actions or declared provider administration.
A never-connected installation retires immediately without inventing cleanup obligations or requiring
deployment cleanup services; configured shared provider profiles are not deleted by hiding the plugin.

Updates build as staged, zero-grant revisions. The administrator reviews the exact completed digest
and declarations separately. Changed digests fail approval. Old connected accounts block activation;
cleanup must finish, and the new revision requires fresh connection/operator/business/agent grants.
There are no credential/policy transfer shims. Direct-network or unusual configuration requests
need a richer administrator review; the simplified page refuses unsupported automatic approval.

## Provenance and erasure

The platform conservatively classifies supported generated records from an agent's actual account
bindings and previously recorded sources. It does not delete all business data or search bodies for
provider names. Original uploaded media and unrelated human-authored primary messages/tasks are
not selected just because an agent can access them. Mixed generated content can be erased: the
disconnect review warns about this explicitly.

Known source IDs in an already authorized native response propagate retention lineage to another
agent in that organization. This does not create an API capability or resource grant. Responses
are bounded, and disconnected source accounts cannot silently start a new derived-data chain.
PostgreSQL account locks fence writes against disconnect; real concurrent-process acceptance is
still required. Historical unclassified records and content without this lineage are not covered.

Disconnect fences authority before cleanup. Vaulted credentials move to revocation quarantine;
the broker cannot use them. Provider failures retry independently of primary deletion. Missing
credentials or an expired revocation deadline do not prove provider revocation and cannot produce
a completed account-removal receipt. A never-authorized, tokenless pending slot has no grant to revoke.

Primary cleanup erases supported inventories, setup snapshots, execution payloads, operational
state, generated artifact content/revisions, messages and agent-authored task content. Unknown
stores block the primary receipt. Work/chat/model records governed by existing reviewed-memory
erasure must finish that workflow; account cleanup cancels pending execution and returns
`PrimaryReviewRequired` rather than bypassing append-only evidence or database triggers.

The retained-evidence adapter now stages eligible recorded operational work through
`MemoryWorkErasure` in its own PostgreSQL transaction. Work/runtime closure must remain inside the
recorded consumer/account scope and pre-disconnect cutoff. Personal recalls, native/private prompt
receipts, unreviewed chat context, unknown expansion, missing attribution and legacy content-bearing
audit evidence remain blocked. The adapter does not weaken EF or PostgreSQL immutability guards.

Captured messages are preserved until their native memory-erasure receipt and source invalidation
exist. `PluginAccountService.CleanupReviews` supplies the existing `MemoryErasureImpactPanel` only
for the original active human who has management access to that account (including scoped operators). Its APIs independently recheck
authority, private audiences, legal holds and the exact reviewed closure. Other administrators do
not receive someone else's private-context review control. This is not yet a complete reviewer
notification/discovery flow for humans who have no plugin-management access.

Provenance capture now precedes source audit capture. Supported provider-derived sources and model
streams use the existing content-omission policy before immutable evidence is written. Historical
content-bearing ledger rows are not retroactively relabelled, silently deleted or declared safe.

Separate durable copy records identify external runtime, processor, memory, backup and retained-
evidence obligations. `IPluginAccountDataDeletionParticipant` is trusted deployment code, not a
plugin callback or a web endpoint to acknowledge deletion. It must be idempotent and return true
only when independently confirmed. The runtime participant uses recorded consumer scope and
server-owned workload locators, invalidates broker authority, destroys old workloads and checks
that inspection returns no workload before confirming.

Completed primary erasure does not imply backup/processor/memory erasure. The page distinguishes
these receipts and displays target/deadline overruns. No override button can manufacture success.
An all-copies receipt is emitted only after every recorded obligation and provider revocation
confirms. These receipts attest the recorded inventory, not unknown historical copies.

## Native memory confirmation

`PluginMemoryDeletionParticipant` implements the provider-neutral `external:memory` participant.
It requires the exact recorded account, organization, consumer and disconnect cutoff. It confirms
conversational capture only after the native erasure fence, cleared extraction/lease state and an
immutable `MemoryErasureReceipt` exist. Generic memory inputs require a receipt and removal by
the native reviewed-erasure transaction; protected turns require their native tombstones.
Missing runtime rows do not prove shutdown: every reset recorded by the receipt must be complete.
Malformed, duplicate-key, legacy ownership, stale, cross-tenant and unrelated receipts cannot confirm.

`CSweetDbContext.CaptureProviderDataAsync` now records generic memory jobs and conversational
capture writes, including durable content-free `memory-origin` episode identifiers. Human-authored
messages do not become disposable company records merely because the agent captures their context.
New native receipts include exact erased-episode identities for descendant/copy confirmation; old
receipts can still confirm their own root, not an unrecorded descendant. The participant reuses
`AgentMemoryReviewService.HasConfirmedErasureAsync` and never acts as a human or overrides holds.
This is confirmation of recorded lineage, not proof that all historical or direct-store writes were captured.

## Non-blocking deployment diagnostics and remaining integration

Missing cleanup configuration does not prevent installation, consent, import or ordinary authorized
work. Administration shows a non-blocking diagnostic. Runtime, native memory and restricted
retained-evidence adapters are provided. Backup/processor adapters report unconfigured until their
deployment trust is configured. Disconnect immediately stops access and independently tracks every
recorded obligation; unconfirmed cleanup remains pending and cannot manufacture a success receipt.

`PluginConnections:AllowDevelopmentLifecyclePreview` is retained as a legacy diagnostic flag only;
it no longer changes readiness or consent behavior. Browser acceptance explicitly uses a Production
account-service environment with that flag disabled and cleanup services initially unconfigured.
Its consent/accounts/erasure receipts remain synthetic, not production attestation.

Existing preview-deployment connections are not automatically proven compliant by registering
adapters. Inventory/backfill, classification coverage, deployment-specific processors/backups,
protected erasure closure, content-bearing audit retention, repeated-failure identity retention,
durable attention/wake notifications and multi-process PostgreSQL races remain release work.
These remaining integrations require validation before claiming verified end-to-end production deletion.

`20261025004315_AddPluginAccountLifecycleEnforcement` is a source migration, not an applied change.
Its rollback refuses to assign a fake human to system-owned maintenance attempts. Review backups
and the deployment migration procedure before applying any migration.

## External confirmation deployment contract

`PluginCleanupOptions` binds to deployment configuration section `PluginCleanup`. Ordinary users
and plugin packages cannot populate it. Configure a `ClientCertificateThumbprint` and exactly one
destination per supported store (`external:backups`, `external:processors`). The built-in transport
uses the server/service identity's CurrentUser/My certificate store, an unexpired certificate with
its private key, OS server TLS validation, no cookies and no redirects. Other deployment certificate
systems may provide a trusted host transport implementation; this is not a plugin capability.

Each destination requires a fixed HTTPS endpoint (no credentials/query/fragment) and 1–8 named
RSA public receipt keys of at least 3072 bits. Public-key PEM, not private keys, belongs in this
configuration. The receiving service must authenticate the client certificate, authorize this
deployment's scope, perform its actual storage/processor deletion, honor retention obligations,
and handle retries idempotently. Configuring an endpoint alone does not implement physical erasure.

`PluginCleanupRequest` version 1 includes copy ID/revision, store, account, optional organization,
consumer, record, and disconnect cutoff. `RequestHash` is uppercase SHA-256 of its web-JSON
serialization with an empty RequestHash. It contains no provider text, access tokens or secrets.
`PluginSignedCleanupConfirmation` returns KeyId plus standard-base64 Payload and Signature. Payload
is the exact UTF-8 web-JSON `PluginCleanupConfirmation` record (version, requestHash, status,
completedAt, expiresAt), signed with RS256 (RSA PKCS#1/SHA-256). Only `Deleted` for the exact request
hash is accepted; timestamps must be current, after the fence, and expire within 24 hours.

No success is inferred from 202, transport failure, missing certificate, malformed/oversized JSON,
unknown key, invalid signature, stale timestamps or a different scope/revision. Retry scheduling
persists on the copy and is concurrency-fenced; retries reuse the same copy/hash. Accepted signed
receipts persist separately and survive a crash before the copy's completion marker. They are
append-only in EF and PostgreSQL. Rollback refuses to discard existing receipt evidence.

`20261025040252_AddPluginCleanupConfirmations` adds the receipt store/retry fence and guards. It has
not been applied. PostgreSQL integration tests are opt-in and were skipped in this environment;
real mTLS endpoint/certificate and external erasure acceptance also remain deployment work.
