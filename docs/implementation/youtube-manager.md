# YouTube Manager implementation

Approved design: a fresh `CSweet.Agent.Platform.YouTube` agent, a deterministic
`CSweet.Plugins.Platform.YouTube` connector plugin, and provider-neutral host enforcement.
Package identities are `com.csweet.agent.platform.youtube` and
`com.csweet.connector.youtube`. Both start at 0.1.0. SDK target: 3.37.0, an additive
release on the inspected 3.36.0 baseline, following the user's approval to use the current
release line in `CSweet.Agent.Sdk`.

## Acceptance ledger

### Implemented foundation (not a complete product)

- SDK connector/dependency/public-OAuth/closed-operation contracts and matching JSON schema.
- Explicit dependency binding, immutable package/profile approvals and native administration endpoints.
- Frozen request materialization and durable plans, including live authority/media revalidation.
- Read-only connector execution with authenticated ownership checks, pinned public-address HTTP,
  secret extraction before runtime delivery, durable results and idempotent retries. Mutations
  remain excluded from the read path; approved non-media actions use the separate durable executor.
  Approved media actions now enter a separate resumable background worker; conversational publishing remains unfinished.
- SDK 3.34.0 adds an explicit fixed `resumable-range.v1` media protocol declaration,
  mirrored by host contracts and frozen request hashing. Unsupported/missing protocols cannot
  prepare a media action. The legacy caller-directed upload handler is now a fail-closed rejection;
  it can no longer send unapproved metadata/media or return raw provider results.
- The new host-only media transport constructs initiation/probe/chunk requests exclusively from
  the frozen media binding. Sessions are constrained to the reviewed initiation origin and path;
  credentials are refreshed/revalidated per exchange, DNS is public-address pinned, redirects,
  cookies and proxies are disabled, and response reads are bounded. Range parsing treats absent
  acknowledgement as zero bytes and rejects duplicate, malformed or impossible offsets. Retry-After
  is bounded without silently shortening provider delays.
- Approved media actions now use host-owned, indexed durable queue records with one fenced exchange
  per dispatch. Initiation, probes and chunks revalidate exact authority. Session URLs are vault-only
  and registered for disconnect cleanup before writes. Restarted/uncertain chunks probe the existing
  session; ambiguous initiation never starts another session. Backoff and no-progress limits are
  durable; changed assets, revoked access and impossible outcomes block. Worker revisions prevent
  a stale worker overwriting its successor. Secret extraction and exact output validation precede
  durable completion, and failed session-secret removal retries independently of the saved result.
  Tests cover dispatch, interruption/shutdown, vault failures, competing claims and schema/SQL generation.
  This is not complete publishing acceptance: the conversational YouTube upload flow, transfer
  pause/cancellation controls, UI progress, broader real-provider recovery and large-file acceptance
  still need implementation/verification. Ingested-byte integrity is now enforced as described below.
- Fixed `conversation.v1` setup assistance: protected-conversation work/tool boundaries,
  text-only LLM requests, native setup actions, durable introductions and one 24-hour reminder.
- Work-claim revalidation prevents queued ordinary work entering a setup-restricted runtime.
- Generic provider settings without Google defaults; profiles are filtered to referenced connectors.
- Database migrations `AddConnectorDependencyPlans` and `AddPluginSetupObligations` generated,
  **not applied** to any database.
  `AddPluginOperationalStateAvailability` adds nullable host queue eligibility and its index;
  its idempotent PostgreSQL script is verified, but it also has **not been applied**.
- The new connector now declares twelve narrow, typed, brokered read operations: account discovery,
  channel/video/individual-comment reads, playlists/items, top-level comment threads and paginated replies, caption metadata, live broadcast
  and stream status, and official aggregate analytics. A thirteenth operation posts a reply to a
  verified top-level comment through the separately granted managed-action controls. It has a
  closed input/output schema, content consent, exact body mapping and authenticated parent ownership
  check. No direct authenticated mutation path is exposed to the connector or agent.
- A fourteenth operation, `youtube.api.video.upload.v1`, now binds an organization asset and explicit
  metadata, privacy, audience/disclosure flags and subscriber-notification choice to the approved
  durable transfer path. Publishing consent has a native settings action. The typed client never
  receives file bytes or session URLs. Result reconciliation independently verifies the channel and
  current video, distinguishes processing from processed results, and flags changed metadata/visibility
  rather than claiming public publication. Unknown outcomes never initiate another upload.
  SDK 3.35.0 and the host now bind media plans to a retained chat attachment, with live access checks.
  The agent's conversation still does not advertise uploading: conversational intake remains unfinished.
  Generic large-video chat attachment controls are wired, with browser acceptance and large-transfer
  recovery hardening still outstanding.
  Scheduled uploads are deliberately not accepted until an execution deadline prevents a missed
  schedule from causing an immediate public release. This is unfinished scope, not a removed requirement.
- Generic host-only connector bootstrap projects authenticated account choices through bounded
  manifest JSON pointers. It rechecks the current step, tenant, digest approval, scopes and grants
  before sending and before accepting results. Duplicate IDs and incomplete pagination fail closed.
- Account confirmation trusts provider-returned names, not browser values. Health validation rereads
  authenticated ownership; ready connections render the declared settings flow. Repeated activation
  validates the original setup record without repeating activation side effects (sequential retry).
- Setup UI uses provider declarations, not YouTube-specific controls. The old hard-coded YouTube
  policy editor/media panel was removed; a generic declarative approval-policy surface is still pending.
- The agent now uses bounded model-backed conversation with persisted-message verification,
  state-only manager preferences, native setup guidance, typed reads and durable requested analytics,
  reply drafts and publication plans. Generated drafts survive delivery failures without regeneration;
  queue insertion/checkpoint gaps recover against exact source-message and installation ownership.
  Information and approval wording do not create phantom work or authorize public changes.
  Manager-authorized pause preserves generated drafts with a durable 15-minute preference check;
  resume requeues up to 25 matching self-owned requests without adding replacement cards.
- Conversational video/caption and playlist-content reads now resolve literal titles/title fragments
  through authenticated channel listings. At most ten pages are read; ambiguous, repeated or incomplete
  listings never auto-select. Choices use escaped titles and YouTube share links, not technical IDs.
  Model-supplied references must occur in the verified current request. Resolved IDs are checkpointed
  before detail reads, so retries retain the same target. This is bounded lookup, not full synchronization
  or a stable snapshot guarantee while external content changes. Relative and ordinal references remain pending.
- Provider-read failures now preserve resumable intake and use bounded, plain-language recovery replies.
  Denied reads offer native connection settings; not-found, usage-limited and unavailable reads do not
  disclose provider diagnostics, invent completion, create unrelated tasks or start retry loops.
- Native setup lists compatible same-organization connector choices without automatically selecting
  an account. Explicit selection checks grants, profile/digest approval and confirmed account readiness.
  Changed bindings revoke autonomous policies. Missing connector installation still requires an
  administrator; automatic Marketplace dependency bundle installation remains outstanding.
- OAuth consent now binds protected state to the exact manifest/digest, setup record, grant revision,
  provider client/endpoints, connection state, requested scopes, redirect, tenant and initiating human.
  The callback rechecks membership and context before exchange and before accepting credentials.
  Only the current declared setup/settings consent action can start authorization.
- Database concurrency checks make each consent attempt single-use across competing contexts and
  prevent a stale callback from changing a concurrently revoked connection back to Connected.
  These are EF concurrency annotations on existing columns; no new database columns were introduced.
- Returned scopes are limited to the explicit request; old and new authorization tokens are not mixed.
  Renewed consent returns channel-bound connectors to account confirmation/validation, invalidates
  existing plans through the grant revision, and revokes standing policies for bound consumers.
  Token exchange rejects duplicate JSON fields/unsupported token types, disables HTTP redirects and
  cookies, and caps buffered responses at 64 KB. Consumed PKCE verifiers are removed before exchange.
- OAuth credential writes use unique authorization-attempt generations with a host-only active
  reference committed alongside Connected status. A competing disconnect rejects the stale commit
  and removes only that attempt's generation. The broker checks live status and the active generation
  before returning or refreshing credentials. Refresh responses reject ambiguous fields/unsupported
  token types and preserve a provider-rotated refresh token; refresh HTTP disables redirects/cookies.
- Disconnect atomically disables the connector, revokes bound consumers' autonomous policies,
  cancels unsent frozen executions/linked pending approvals, clears account metadata and broker result copies, and
  records host-only credential cleanup before remote work. A background worker quarantines revocation
  credentials outside the active broker slot, retries interruption/provider failure, refuses changed
  provider destinations and removes quarantined material at its seven-day deadline even if remote
  revocation remains unconfirmed. Reconnect is blocked while cleanup is pending. The UI previews
  affected agents and distinguishes pending/local cleanup from confirmed provider revocation.
  This does not yet purge provider-derived copies in consuming-agent drafts, reports and conversations.
  In-flight actions become Indeterminate; confirmed and already uncertain outcomes are not relabeled
  Cancelled. Content-free status evidence survives result purge. Revoked result access returns
  Unavailable rather than leaking provider copies or falsely claiming the action never reached YouTube.
- Frozen connector plans now enter the existing managed-action approval record with exact plan,
  account, resource, revision and idempotency bindings. The host selects the current assigned manager
  or accountable CEO; a general owner permission does not override a different assigned approver.
  Human API decisions and agent decisions use the same service, durable receipt and optimistic plan
  revision. Native review includes escaped exact request changes, query-only targets and media digest.
  Approval records do not themselves perform provider HTTP; plain-text conversation remains insufficient.
- A host-internal non-media mutation executor rechecks approvals and live authority before every
  ownership read and outbound request, extracts declared response secrets, validates response schemas
  and saves confirmed results before reporting completion. It claims each approved plan once. Failed
  preflights block; uncertain sends, cancellation or malformed responses become Indeterminate and
  cannot automatically resend. Interrupted Executing records also cannot be reclaimed as new actions.
  A scoped durable worker now consumes Approved actions and converts abandoned Executing records
  to Indeterminate without resending. Failures before dispatch block visibly through action status.
- SDK 3.32.0 exposes `Platform.Connectors.RequestActionAsync` and `ReadActionAsync` with typed
  requests and receipts. The hidden host controls enforce their own live grants in addition to
  the provider operation grant. The caller cannot select credentials, destinations or installations.
  Terminal result reads revalidate the original package, account and grants before releasing data.
- Durable action events wake only the exact requesting installation, with both manifest subscription
  and event grant checked. Agent approvers receive the existing structured approval-request event
  when subscribed and granted. Stable outbox/inbox correlations recover checkpoint gaps without
  duplicate work. Human approvers now receive native cards in an exact two-participant protected chat,
  with an idempotent notification and host-owned message correlation. Copied correlations cannot create
  cards in other messages/chats. Approve, Request revision and Reject use the same authoritative decision
  endpoint; revision feedback is durable and displayed on the card. Native escaped rendering is tested.
  Business-facing summaries use the reviewed connector description, with technical details expandable.
  The comment-reply workflow now uses this lifecycle; other YouTube mutations remain pending.
- SDK 3.33.0 adds authoritative decision feedback to action receipts and independently granted,
  model-hidden cancellation. The host validates original result authority before releasing feedback.
  Cancellation records its receipt and event with the execution revision; a competing execution claim
  wins or loses atomically. Executing, Completed and Indeterminate states cannot be cancelled away.
  Cancellation never undoes a provider effect or authorizes a fresh-key retry. Tests cover grant/tenant
  isolation, idempotency, pending/approved cancellation, concurrency and revoked feedback access.
- The first conversational mutation slice is a reviewed comment reply. A supplied comment share link
  is source-verified, the parent is read through the connector, and the generated draft is saved before
  preview or approval request. Exact action links, durable scheduled checks and correlated events resume
  the work. Structured revision feedback generates a saved new revision requiring a new exact approval;
  prose approval never authorizes posting. Request/checkpoint and notification retries reuse stable keys.
  Pause cancels known pending/approved replies, never in-flight sends; resume cannot restore cancelled actions.
  Per-parent reservations prevent another conversational request from creating a concurrent or uncertain
  replacement send. Completed output must match the exact parent and draft before announcing success.
- Uncertain replies receive bounded, read-only reconciliation of the connected channel and paginated
  replies. Matching text/author is only a candidate, never proof that this action succeeded; absence is
  never permission to retry. Candidate links and incomplete-scan reasons are saved for manual review.
  Revoked access remains blocked without a false cancellation or posting claim. Other-agent approvers'
  business-context projection, broader reply target discovery and scheduled engagement synchronization remain pending.
- Requested reply drafting now uses an agent-owned durable scan of published threads and paginated
  replies. Each dispatch processes one provider page, revalidates the channel, shards normalized text,
  freezes the page, upserts channel/comment inbox pointers and advances only after a durable receipt.
  Per-scan markers deduplicate overlapping pages and reject repeated cursors. Lost cursor/inbox writes
  replay the frozen page; cancellation before freezing leaves the cursor unchanged. Unicode shards
  preserve surrogate pairs and fit host state bounds. Provider limits/unavailability defer an hour;
  access/ownership failures block for recovery. After traversal the agent reviews every unique returned
  comment and reply from full saved text in resumable batches of at most two, persisting a bounded
  Draft/NoReply/NeedsReview decision before any delivery. Known channel-authored comments and empty
  text need no model guess. Review messages remain in the source conversation, escape external markup,
  split at rendered message-size bounds without truncating drafts, and reuse batch receipts and message
  keys after failures. Final review counts must reconcile with the scan. Tests cover more than five
  comments, full Unicode evidence, large escaped drafts, malformed model decisions, and lost batch/cursor
  checkpoints. Empty results create no model-generated draft. No draft decision grants posting authority.
  This path adds no YouTube host logic or new grants. Cross-turn selection/revision of saved batches,
  deleted/hidden-item reconciliation, urgent semantic alerts and retention of the new provider-derived
  records still need implementation.
- Host onboarding now starts an agent-owned recurring duty after verifying its exact organization,
  employee and channel. Introduction and task are durable before lifecycle acknowledgement; replay
  does not duplicate either. The same duty runs initial sync and periodic full traversals, with default
  15-minute cadence after completion, one-minute page continuations, pause/cadence preferences, hourly
  access/quota recovery and no process timers. Independent comparison checkpoints preserve new/change
  notifications even when a requested review also updates the inbox. Daily inbox-change count digests
  use configured local days (UTC default), remain in the onboarding conversation, and suppress unchanged
  days. Saved cycle receipts recover lost delivery/cursor writes and past-due next-check times.
  This is not automatic semantic drafting, urgent escalation, weekly analytics or dynamic approver
  routing. Native setup explicitly limits this preview to authorized test channels until data cleanup
  and real-provider acceptance are complete. No production-retention claim is made.

### Retained media provenance (SDK 3.35.0)

Authoritative chat attachment metadata now includes an optional organization `MediaAssetId`, never
storage paths or bytes. `RequestConnectorAction.MediaSource` carries the exact conversation/message/
attachment tuple outside the provider input. The host requires current chat-read permission and active
employee membership, verifies all source relationships and matching checksum/size/type, then freezes
the source and file name into the plan. Every transfer exchange and result read revalidates the source;
missing sources, revoked access and metadata changes fail closed. Non-media requests omit the optional
field, preserving their request shape. Old unsourced media plans are deliberately not executable.

Agent message forwarding now requires already visible retained media, currently authorized project
work, or the agent's own unscoped media. It cannot manufacture an authorized source by attaching a
guessed organization asset ID to its own conversation. Tests cover this laundering attempt, lost read
grants, removed membership, foreign IDs and changed approved metadata, including revocation between
upload exchanges. Human attachment behavior and plain-text setup assistance remain unchanged.

This is not complete publishing. The generic chat composer now accepts MP4/WebM videos within the
deployment file limit (capped at 256 GiB), plus images, documents and captions. Non-video attachments
retain the 25 MiB per-file / 50 MiB aggregate limit, and all messages retain the eight-attachment limit.
Both communication messages and direct-agent turns enforce the shared policy on the server.

The native attachment component reads bounded Blob slices, hashes the selected file, retains a
conversation-scoped recovery record in session storage and exposes pause/resume/remove controls.
Reload recovery requires reselecting the original file and verifying its full checksum. A stable,
organization-scoped create identity and authoritative session reads recover lost create, chunk and
completion responses without deliberately creating another upload. Upload results and pending
attachment drafts remain scoped to the originating conversation, including asynchronous send failure.
Removing an upload does not delete a file already finalized in organization storage. The unused old
uploader has been removed; no media records or user files were deleted.

Deterministic client tests cover bounded buffers, lost responses, wrong-file/foreign-conversation
recovery, malformed progress, checksum mismatch, cancellation and deployment limits. JavaScript
tests cover synthetic offsets above 2 GiB, bounded slices, retained file references and checkpoint
isolation. These are not browser acceptance or an actual 256 GiB transfer. Server finalization remains
synchronous; cross-node quota reservation, crash gaps between asset creation/session completion,
transfer controls, scheduling deadlines and real-provider
recovery remain outstanding. The agent's conversational publishing workflow remains disabled until
the corresponding end-to-end safety and recovery work is complete.

### Ingested media byte integrity

Host ingestion now hashes the exact stream consumed by storage, recording both the whole-file digest
and fixed 8 MiB chunk digests. The host checks the storage result against that whole-file digest and
persists chunk records with the asset. Storage that does not consume the full stream or returns a
different digest cannot create a usable asset. Chunk records are internal host metadata; they are
not plugin grants, provider operations or user-facing setup details.

Before an approved resumable transfer begins, the worker requires a complete, contiguous set of
ingestion records bound to the approved asset digest. Before sending each range, it reads and hashes
the corresponding original chunks and copies only verified bytes into the outbound buffer. Unaligned
provider resume positions verify both intersecting chunks. Changing bytes while leaving the asset's
recorded checksum untouched now stops the transfer without sending the altered chunk. Memory usage
is bounded by chunk size, not asset size. Missing or inconsistent records fail closed; historical or
otherwise imported assets without ingestion proofs must be uploaded again before external transfer.
There is no automatic backfill that would trust potentially changed historical bytes.

Migration `20260908004653_AddMediaAssetChunkIntegrity` adds the asset-keyed integrity table and cascades
its records on asset deletion. Generated SQL and snapshot/current-model consistency are verified
offline. The migration has not been applied to a live database. This closes the previously documented
metadata-only byte-check gap for the new ingestion/connector transfer path; it does not complete
cross-store finalization transactions, all imported/generated asset paths, transfer cancellation,
retention, browser acceptance or real-provider upload acceptance.

### Attachment-grounded publication planning

Publication planning in the agent now captures validated video descriptors from the authenticated
source message before adding durable personal work. Saved attachment/message/asset/author/hash
bindings survive retries and are rechecked before generation or redelivery. Changed or unavailable
sources block without replacing the video. Model context includes file names, types and sizes, not
opaque asset or attachment IDs, and explicitly distinguishes descriptors from viewed video contents.
A typed source selector also rejects ambiguous file-name matches for the upcoming upload intake.
This increment does not advertise or enable conversational uploads: metadata questions, progressive
publishing consent, action correlation, approval/revision handling and verified completion still need
integration into that conversation. The agent passes 92 deterministic tests, its self-test and a local
0.1.0 pack against package-only SDK 3.35.0 and `CSweet.Plugins.Platform.YouTube` 0.1.0. No Google work
was performed and no package was published.

### Prepared upload worker (initial increment; conversation integration follows below)

The agent now processes owner-correlated personal work containing a previously saved video source,
channel and exact upload metadata. It verifies the source/channel, reserves the channel/file digest,
persists a preview before requesting approval, and recovers a lost action checkpoint with the same
idempotency key. The worker reads authoritative decisions rather than trusting event status hints.
Only pending uploads can be cancelled by pause; in-flight transfers remain tracked. Completed broker
actions go through the plugin's channel/video reconciliation before a success notice is saved.
Processing checks use durable waits, send one receipt notice, and escalate the existing video after
two days without confirmed processing. Uncertain, unavailable or revision-requested outcomes do not
start replacement uploads. Final notices are saved and delivered idempotently.

The manifest now requests the narrow upload dependency grant and extends existing action read/cancel
purposes. This declaration does not grant authority or trigger provider consent. **At this increment,
conversational upload intake remained disabled.** Tests supply prepared work records; metadata collection, category
selection, consent guidance, draft revision/resubmission, intentional duplicate confirmation and
in-flight controls remain required. The new worker is not an end-to-end publishing acceptance claim.
The current agent passes 104 deterministic tests, self-test and local pack at 0.1.0 against package-only
SDK 3.35.0 and plugin 0.1.0; no Google calls or publication occurred.

### Provider-owned category discovery

The plugin now declares a fifteenth operation, `youtube.api.video-category.list.v1`, using the fixed
YouTube category endpoint with explicit country/display-language inputs and a closed field mask.
Its typed client resolves provider-returned display names only when exactly one current category is
assignable. Invalid inputs, malformed/duplicate catalogs, unassignable names and unexpected
continuation tokens fail closed; it never guesses a category ID or invents a page-token request.
Categories are public provider metadata, not company-owned resources, so the category creator's
channel is not used as an ownership check. All existing channel-content ownership checks remain.
The request follows [videoCategories.list](https://developers.google.com/youtube/v3/docs/videoCategories/list)
and the [category resource](https://developers.google.com/youtube/v3/docs/videoCategories).

Prepared upload work now requires the saved country/language/category-name selection to resolve to
the exact category ID in its saved request before approval preparation. The preview includes the
provider category name and tags. Conversational category questions and the complete upload intake
remain pending. These changes required no provider-specific host code or SDK change. Verification:
47 plugin tests, 107 agent tests, both self-tests and both local 0.1.0 packs pass. The agent was restored
into a fresh package cache against the updated plugin and SDK 3.35.0, with no sibling references.
No real Google request, deployment, package publication or migration application occurred.

### Conversational immediate-upload integration

Immediate-upload requests now collect a verified video attachment and missing title, description,
country/category, privacy, audience, altered/synthetic disclosure, subscriber notification and timing
choices in the source conversation. The source-bound intake and extraction checkpoints survive agent
restarts. Follow-ups reuse one personal obligation, bind to the requester/conversation and original
upload used for routing, and cannot overwrite newer answers. Videos can arrive on a later message.
Missing publication choices are never defaulted. Category labels are rendered as untrusted quoted
data; category IDs and media asset IDs remain internal.

Once details are complete, the agent saves prepared work before waking the existing upload worker.
Tests exercise conversation through exact approval request, authoritative completed result, verified
video read and one durable completion notice. Chat approval never changes the authoritative action.
Lost merge checkpoints reuse the saved extraction even when the latest conversation pointer changes.
Missing provider access offers native connection settings; usage limits and temporary failures retain
details without misrepresenting the problem as missing consent. Platform state failures are not caught
as provider errors. Missing publishing grants at worker execution offer native recovery guidance;
temporary execution failures retain the same durable work with delayed checks.

This increment passes **124 agent tests and 47 plugin tests**, both executable self-tests and local
0.1.0 packs. A fresh package-only agent restore uses SDK 3.35.0 and
CSweet.Plugins.Platform.YouTube 0.1.0; the cached plugin archive matches the freshly packed archive.
Both packages' embedded README/manifest content and NuGet identities/dependencies were inspected.
No SDK contracts or host implementation changed in this increment. Host/browser tests were not rerun.
These are deterministic integration tests, not live model evaluations or browser/Google acceptance.
Scheduling, revision/resubmission, deliberate repeat uploads, in-flight controls, automatic OAuth
completion wake-up and the remaining feature/retention/security acceptance work are still incomplete.

### Upload metadata revisions

The agent now handles authoritative upload revision decisions using saved, typed metadata candidates.
Each new revision retains the original media source and confirmed channel, records the old request,
and receives a different approval/idempotency key. Generated candidates are checkpointed before
category validation and the exact previous decision is reread before saving the revised request.
Before submitting a new approval, the worker again verifies that the old action remains in revision
requested state; executing, completed, uncertain or approved prior actions block substitution.
Revision-specific preview, access, processing and result keys avoid suppressing later review cards or
duplicating completion notices. Old action events cannot wake the replacement as if they approved it.

Unchanged drafts, video substitutions and unsupported schedules ask for clarification without another
upload. The original requester may answer in the same conversation. Clarifications bind to the exact
action being revised before generation, so replaying an answer after a newer review exists cannot
apply it to that review. Paused preparation does not generate revisions. A bounded twenty-revision
limit escalates to manual review rather than looping indefinitely. Published-video editing, replacing
the selected file, scheduling and in-flight upload controls remain separate unfinished work.

Verification for this revision increment: **135 agent tests and 47 plugin tests**, both self-tests
and both local 0.1.0 packs pass. A fresh package-only restore uses SDK 3.35.0 and the current
CSweet.Plugins.Platform.YouTube package. Tests include conversation clarification, replay against a
newer review, changed prior decisions, lost candidate-to-work checkpoints, pause, no-op/file/schedule
clarifications, distinct previews and one verified completion notice for the revised upload.
No host implementation or SDK contract changed, and no live Google request was made.

### Response ownership for implicit-account endpoints

SDK 3.37.0 and the host now support `http.responseResourcePointers`, a protocol-2.2 declaration
for endpoints that derive their account from authorization rather than a request account selector.
Each selected response string must exactly match the frozen confirmed resource. At most eight unique
pointers of 256 characters are allowed, with one array wildcard each. Existing empty arrays are valid;
missing paths, non-array wildcard targets, non-string owners, duplicate JSON keys and mixed owners
fail closed. Bootstrap cannot use these checks. Older hosts reject the required protocol 2.2 rather
than ignoring an unknown security declaration. Both host import and preview recognize the new version.

The host freezes these checks into approved plans and enforces them before secret extraction and
result persistence/delivery on reads, mutations and final media responses. A mutation with a wrong
response owner is indeterminate, never successful or automatically retried. Existing plans without
the feature retain their serialized request shape. The implementation contains no YouTube-specific
endpoint, scope, field or category; tests use a separate generic example provider.

This prerequisite came from checking the documented membership endpoints: they act on the authorizing
channel and return creator-channel ownership in each record, rather than accepting a channel selector.
Membership access also requires provider channel-level eligibility, not consent alone. See Google's
[members resource](https://developers.google.com/youtube/v3/docs/members),
[member listing](https://developers.google.com/youtube/v3/docs/members/list) and
[level listing](https://developers.google.com/youtube/v3/docs/membershipsLevels/list).
Membership operations were not enabled by this prerequisite alone; the bounded membership-read
increment below now supplies the exact contracts and conversation behavior. No unsupported channel
query is added.

Verification: 173 SDK tests, 2 sample tests, 7 temporary-template tests and its self-test, 216 selected
host connector/security/provider-administration/import-preview tests, 135 agent tests and 47 plugin
tests pass. Both YouTube self-tests pass. SDK 3.37.0 and both 0.1.0 packages were packed locally;
NuGet identities/dependencies and embedded READMEs/schema/manifests match source. The host and both
YouTube consumers use package-only dependencies; their SDK assembly hashes match the verified SDK
build. SDK defaults, templates, package tests and changed downstream pins are synchronized. The five
existing nullable UI warnings remain. No deployment, migration application or real-Google acceptance
occurred; the broader acceptance checklist remains open.

### Restricted membership reads and conversational paging

The plugin now declares `youtube.api.member.list.v1` and `youtube.api.membership-level.list.v1`.
Both use fixed GET requests, closed schemas, independent consuming-agent grants, optional membership
scopes and protocol-2.2 response ownership checks against every creator channel. The current-member
operation uses `all_current` and pages of at most 25, with only a bounded page-token input; levels have
no pagination input. Missing member profiles remain records, not discarded or invented identities.
Profile URLs and images are not collected. Successful empty level lists are not eligibility failures.
Native progressive consent explains Google's separate restricted-channel eligibility and a Partner
Manager handoff. Consent alone never enables an ineligible channel.

The agent answers member/level questions and "show the next page" without exposing technical IDs or
page tokens to its reasoning model. Cursor state is scoped to the source conversation and requester,
freezes the confirmed channel before reads, and advances only after evidence is saved. Replays cannot
rewind newer cursors; failed cursor writes reuse the saved response. Repeated page tokens, changed
channels and unverifiable ownership produce plain-language recovery guidance, not false completion.
The model is told not to report a bounded page as a channel total or infer missing identities.
Permission denial offers native settings; temporary errors and usage limits retain the request and
never become an invented empty list. These informational requests create no personal work or mutations.

The plugin has seventeen operations: fifteen reads (including bootstrap) and two exact-approval
mutations. Membership updates feeds, bulk synchronization, mutations, full retention/purge and
real-Google restricted-API acceptance are not claimed by this increment.

Verification for this increment: all 144 agent tests and 57 plugin tests pass, including membership
paging/recovery and exact manifest checks. Both executable self-tests pass. Both 0.1.0 NuGet packages
were packed locally and their identities, SDK 3.37.0/dependency pins and embedded READMEs/manifests
were checked against source. The agent suite also passes with a fresh package-only restore; its
cached plugin assembly hash matches the verified plugin build. No SDK contract was changed by this
increment. No package was published, database migration applied or real-provider request performed.

### Provider-neutral administration boundary

The host's shipped configuration no longer registers a Google/YouTube OAuth profile. Deployment
administrators may still supply approved vault-backed or deployment-managed profiles; removing the
empty built-in default does not delete configured credentials. Public provider names and destinations
come from installed or pending connector manifests. The native administrator UI offers configuration
only for those declarations, keeps destinations read-only, suppresses unrelated profiles, and refuses
conflicting package metadata or a mismatch with an existing profile. Rendering never creates a
profile, approves a package, grants an agent access, or initiates consent. Credential forms open only
after an explicit administrator action; ordinary users use the connector's account setup flow.

Rendered-component regression tests cover no connector, unrelated agents, two fake providers,
escaped external text, conflicting declarations, and credential-profile destination mismatches.
These are deterministic native-component tests, not interactive browser acceptance. Profile settings
remain an administrator-only surface; the existing package-digest/profile approval service, not UI
metadata, is the authority boundary.

The host engagement-ingestion handler now persists records only. It no longer interprets urgency,
composes provider-specific notifications, sends messages, or creates digest receipts. Those effects
require the consumer's independently granted communication and durable-work capabilities. Regression
tests exercise two providers, repeated ingestion, urgent metadata and a legacy digest payload without
any implicit chat/work effect. The new agent owns its monitoring and count digests.

The legacy caller-defined `platform.managed-action.execute.v1` route is now a fail-closed rejection,
including direct handler calls with historical approvals and autonomous policies. New mutations must
use the declared connector operation, frozen plan and exact approval path; historical proposal
decisions remain separate and cannot enable this retired authorization route. Unknown capabilities
also cannot fall through to checkpoint persistence. No existing records or credentials were deleted.

The deterministic connector has been consolidated into the existing `CSweet.Plugins.Platform.YouTube`
repository and NuGet package; the agent now consumes that package instead of
`CSweet.Plugin.Connector.YouTube`. The connector identity remains `com.csweet.connector.youtube`,
kind `connector`, version 0.1.0. Its reviewed manifest still supplies provider setup, scope sets,
typed abilities and request mappings; installation and dependencies grant nothing implicitly.
Package updates still require digest/profile approval. The former repository is historical reference,
not a dependency of the new agent. No credentials, bindings or autonomous policies are migrated.

The unused `PluginStandingPolicyService`, its provider-shaped public contracts and its administrative
GET/PUT/DELETE endpoints have now been removed. This removes host-owned category/action names and
privacy enumerations; it also stops accepting policies that never authorized the new connector path.
Historical database records and disconnect/account-change revocation remain intact. No records,
credentials or historical repositories were deleted or migrated. Endpoint construction tests reject
the obsolete route, and connector execution tests prove that a historical approved autonomous policy
cannot bypass a fresh exact CEO decision. Existing caller-defined action routes remain fail-closed.

This is retirement of an unused authorization model, **not completion of autonomous execution**.
Declarative human-owner-approved standing policies must still bind the reviewed connector package,
consumer grants, account, permitted operation/effect and bounded request constraints; their decisions
must authorize frozen plans and be rechecked at execution. Hard-gated effects still require explicit
decisions. No application-name switch or arbitrary executable policy expression may be introduced.
Until that model is implemented, the Fully Autonomous preference falls back to exact CEO approval.
The old first-party catalog entry remains pending replacement readiness. The full provider-neutral
product acceptance checklist is still open.

After retirement, 259 selected host connector, OAuth, cleanup, approval, import, provider-administration,
setup-endpoint and legacy-boundary tests pass with external sibling project references disabled.
The new historical-policy regression exercises refusal before a decision and successful deterministic
execution only after the exact CEO decision. The removed service/interface were tracked source files,
recoverable from Git; the historical policy database table was not removed or repurposed.

Current verification: 332 selected host connector/setup/OAuth/cleanup/approval/capability-registry/chat/provider-administration/onboarding/legacy-boundary/media-source/attachment-access/resumable-upload/media-integrity/GenAI
tests pass, including exact-plan decisions, typed controls, durable dispatch, event replay,
result isolation, cancellation/feedback and native-review routing/rendering. Legacy OAuth fixtures now
include the authenticated human required by consent enforcement; no security check was weakened.
The generic attachment changes also pass four Node tests for the browser file/recovery helper. These
are JavaScript unit tests, not native browser end-to-end acceptance. No SDK, agent or plugin package
changed in this attachment increment; the package verification below is from the preceding increment.
SDK **3.35.0** passes 152 SDK tests, 2 sample tests and the temporary standalone-template verification
(7 generated tests plus self-test). The agent's 107 tests and connector's 47 tests pass with package-only
restores; both executable self-tests pass. Host builds use **all external sibling
project references disabled** and isolated outputs. Five nullable warnings remain in concurrently
edited `Communications.razor`; the YouTube changes compile. SDK 3.35.0, connector 0.1.0 and agent
0.1.0 have been packed locally; archive metadata confirms the SDK and connector downstream pins.
After consolidation, the renamed plugin's 36 tests and agent's 81 tests pass, both self-tests pass,
and both 0.1.0 archives were repacked and inspected. The agent's resolved NuGet graph contains
`CSweet.Plugins.Platform.YouTube/0.1.0` and no former connector dependency or sibling project reference.
The renamed plugin now declares fifteen operations including the provider category catalog; its upload client requires
attachment provenance and depends on SDK 3.35.0. Archive inspection confirms SDK 3.35.0 and plugin/agent
0.1.0 metadata, synchronized dependencies and embedded READMEs. The agent's restored graph contains
SDK 3.35.0, the renamed plugin 0.1.0 and WorkManagement.Contracts 3.16.0, without sibling references.
Prior foundation verification covered
109 selected host tests, the previous SDK/template suite and Memory.Broker 0.1.3; those historical
counts are not claims that the entire current suite was rerun. Nothing was published, no migration
was applied, and no Google credentials were used. These are deterministic foundation checks,
not full browser acceptance or real-provider verification.

Next implementation priorities: remove the audited legacy host domain responsibilities as their
agent/connector replacements are implemented; typed conversational publishing through the new durable media worker,
including transfer lifecycle controls and media attachment selection;
the remaining typed YouTube mutation workflows; bundle installation and CEO-to-human setup handoff; complete channel synchronization
and review/report cadences; remaining typed YouTube operations; provenance-aware data purge and broader
recovery/acceptance. Conversational reads currently persist a bounded single response; large pages can
exceed the platform's 64 KB operating-state payload limit and need sharded evidence/artifact storage.

The complete implementation still requires integrated product acceptance of the reply workflow, the remaining mutation workflows and media jobs;
complete setup/CEO handoff and bundle UI; the remaining YouTube operations and scheduled
conversational work; OAuth/retention/recovery hardening; and full product acceptance. Account discovery currently
rejects additional pages instead of offering paginated selection, avatars are not yet displayed,
and broader channel-metadata synchronization remains incomplete. The agent's engagement scan does
paginate published threads and their replies; held/spam and deleted/hidden reconciliation remain
unfinished. Bounded member/level reads are implemented; membership updates/synchronization and partner
operations remain incomplete.
Distributed refresh serialization, orphan-generation reconciliation after arbitrary vault/database
failures, consent issuer mix-up defenses, and tracked retention/purge still require implementation.
Generation references isolate failed writes but are not a general cross-store transaction protocol.
The new disconnect job provides durable revocation recovery, not complete data-retention compliance.
In-flight old-format OAuth
states intentionally fail after this update and require starting consent again. This is not a claim
of complete RFC 9700 compliance or production-ready OAuth.

OAuth review references: [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html) and
[Google web-server authorization](https://developers.google.com/identity/protocols/oauth2/web-server).
Transfer protocol reference: [Google resumable upload protocol](https://developers.google.com/youtube/v3/guides/using_resumable_upload_protocol).

### Full acceptance

- [ ] SDK protocol 2.1 connector/dependency/operation contracts and validation
- [ ] Host dependency binding, package/profile approval, installation lifecycle
- [ ] Prepare/approve/execute request enforcement and resource ownership checks
- [ ] Restricted conversational setup and safe generic settings renderer
- [ ] Deterministic YouTube connector (content, uploads, engagement, live, memberships, partner, analytics)
- [ ] Conversational agent with durable setup and personal agenda
- [ ] Quotas, ambiguous-outcome reconciliation, retention and purge
- [ ] Marketplace guided bundle and retirement of old active references
- [ ] Automated security, integration, agent and browser acceptance
- [ ] Package version synchronization, tests, pack and package-only consumer verification
- [ ] Real Google acceptance (requires authorized external test accounts)

No completion claim is made by this ledger until the corresponding behavior is
implemented and verified. Existing unrelated changes in the workspace must be preserved.

## Security invariants

The host owns credentials and decisions. Connectors never receive tokens. Agents
never get authenticated raw HTTP. An approved request plan binds the requester,
connector package digest, connection, channel, resource, input and media digests,
revision, and idempotency identity. Provider requests must match approved steps.
Scope consent does not imply a consumer grant. Any uncertain mutation result is
reconciled or explicitly blocked, never blindly retried. Disconnect invalidates
work immediately and durably purges provider data. Setup assistance can converse
before activation, but cannot execute normal external work.
