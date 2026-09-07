# YouTube Manager implementation

Approved design: a fresh `CSweet.Agent.Platform.YouTube` agent, a deterministic
`CSweet.Plugin.Connector.YouTube` connector, and provider-neutral host enforcement.
Package identities are `com.csweet.agent.platform.youtube` and
`com.csweet.connector.youtube`. Both start at 0.1.0. SDK target: 3.31.1, following the
user's approval to use the current release line in `CSweet.Agent.Sdk`.

## Acceptance ledger

### Implemented foundation (not a complete product)

- SDK connector/dependency/public-OAuth/closed-operation contracts and matching JSON schema.
- Explicit dependency binding, immutable package/profile approvals and native administration endpoints.
- Frozen request materialization and durable plans, including live authority/media revalidation.
- Read-only connector execution with authenticated ownership checks, pinned public-address HTTP,
  secret extraction before runtime delivery, durable results and idempotent retries. Mutations
  and media transfers remain blocked until their approved-action executor is implemented.
- Fixed `conversation.v1` setup assistance: protected-conversation work/tool boundaries,
  text-only LLM requests, native setup actions, durable introductions and one 24-hour reminder.
- Work-claim revalidation prevents queued ordinary work entering a setup-restricted runtime.
- Generic provider settings without Google defaults; profiles are filtered to referenced connectors.
- Database migrations `AddConnectorDependencyPlans` and `AddPluginSetupObligations` generated,
  **not applied** to any database.
- The new connector now declares eleven narrow, typed, brokered read operations: account discovery,
  channel/video reads, playlists/items, top-level comment threads and paginated replies, caption metadata, live broadcast
  and stream status, and official aggregate analytics. No mutation capability is advertised.
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
  cancels frozen executions/linked approvals, clears account metadata and broker result copies, and
  records host-only credential cleanup before remote work. A background worker quarantines revocation
  credentials outside the active broker slot, retries interruption/provider failure, refuses changed
  provider destinations and removes quarantined material at its seven-day deadline even if remote
  revocation remains unconfirmed. Reconnect is blocked while cleanup is pending. The UI previews
  affected agents and distinguishes pending/local cleanup from confirmed provider revocation.
  This does not yet purge provider-derived copies in consuming-agent drafts, reports and conversations.
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
  This executor is not yet connected to a durable dispatch worker or exposed as an agent request tool.
  Approval notification obligations are stored, but protected-chat cards and event dispatch remain pending.

Current verification: 118 selected host connector/setup/OAuth/cleanup/approval tests pass, including
23 exact-plan approval/mutation tests and native-review routing coverage. Legacy OAuth fixtures now
include the authenticated human required by consent enforcement; no security check was weakened.
The previous verification also passed 21 conversational-agent tests and 8 connector tests against
SDK **3.31.1**; those unchanged package suites were not rerun during the host approval work. Host builds use **all external sibling
project references disabled** and isolated outputs. Five nullable warnings remain in concurrently
edited `Communications.razor`; the YouTube changes compile. SDK 3.31.1 and connector 0.1.0 have
been packed locally into the current verification feed. Prior foundation verification covered
109 selected host tests, the previous SDK/template suite and Memory.Broker 0.1.3; those historical
counts are not claims that the entire current suite was rerun. Nothing was published, no migration
was applied, and no Google credentials were used. These are deterministic foundation checks,
not full browser acceptance or real-provider verification.

Next implementation priorities: expose typed action request/read contracts, wire durable execution
and protected-conversation approval events/cards, then durable media jobs; bundle installation and CEO-to-human setup handoff; complete channel synchronization
and review/report cadences; remaining typed YouTube operations; provenance-aware data purge and broader
recovery/acceptance. Conversational reads currently persist a bounded single response; large pages can
exceed the platform's 64 KB operating-state payload limit and need sharded evidence/artifact storage.

The complete implementation still requires end-to-end mutation approval/dispatch/reconciliation and media jobs;
complete setup/CEO handoff and bundle UI; the remaining YouTube operations and scheduled
conversational work; OAuth/retention/recovery hardening; and full product acceptance. Account discovery currently
rejects additional pages instead of offering paginated selection, avatars are not yet displayed,
and comment-thread reads do not synchronize all replies. Memberships/partner operations are absent.
Distributed refresh serialization, orphan-generation reconciliation after arbitrary vault/database
failures, consent issuer mix-up defenses, and tracked retention/purge still require implementation.
Generation references isolate failed writes but are not a general cross-store transaction protocol.
The new disconnect job provides durable revocation recovery, not complete data-retention compliance.
In-flight old-format OAuth
states intentionally fail after this update and require starting consent again. This is not a claim
of complete RFC 9700 compliance or production-ready OAuth.

OAuth review references: [RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html) and
[Google web-server authorization](https://developers.google.com/identity/protocols/oauth2/web-server).

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
