# YouTube Manager implementation

Approved design: a fresh `CSweet.Agent.Platform.YouTube` agent, a deterministic
`CSweet.Plugin.Connector.YouTube` connector, and provider-neutral host enforcement.
Package identities are `com.csweet.agent.platform.youtube` and
`com.csweet.connector.youtube`. Both start at 0.1.0. SDK target: 3.30.0.

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
- The new connector now declares ten narrow, typed, brokered read operations: account discovery,
  channel/video reads, playlists/items, top-level comment threads, caption metadata, live broadcast
  and stream status, and official aggregate analytics. No mutation capability is advertised.
- Generic host-only connector bootstrap projects authenticated account choices through bounded
  manifest JSON pointers. It rechecks the current step, tenant, digest approval, scopes and grants
  before sending and before accepting results. Duplicate IDs and incomplete pagination fail closed.
- Account confirmation trusts provider-returned names, not browser values. Health validation rereads
  authenticated ownership; ready connections render the declared settings flow. Repeated activation
  validates the original setup record without repeating activation side effects (sequential retry).
- Setup UI uses provider declarations, not YouTube-specific controls. The old hard-coded YouTube
  policy editor/media panel was removed; a generic declarative approval-policy surface is still pending.
- The conversational agent repository remains scaffold-only.
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

The user approved targeting the shared SDK **3.30.0** release. Concurrent Git file-lock
changes are preserved. Current verification: 129 SDK tests, 2 sample tests, 7 generated-template
tests and template self-test passed; selected host tests passed 109/109, including 19 new OAuth
security cases and an isolated SQLite single-use concurrency check. The YouTube connector's
7 tests and manifest self-test passed against the freshly packed SDK in a separate package cache.
SDK 3.30.0 and connector 0.1.0 were packed locally; versions and packaged manifest were inspected.
The API builds with **all external sibling project references disabled**, using the local feed
(zero warnings/errors). Prior foundation verification also covered 20 memory tests and packed
Memory.Broker 0.1.3; its corrected SDK pin remains 3.30.0. Nothing was published, no migration was
applied, and no Google credentials were used. These are deterministic read-slice/foundation checks,
not full browser acceptance or real-provider verification.

The complete implementation still requires mutation approvals/execution and media jobs;
complete setup/CEO handoff and bundle UI; the remaining YouTube operations and conversational
agent; OAuth/retention/recovery hardening; and full product acceptance. Account discovery currently
rejects additional pages instead of offering paginated selection, avatars are not yet displayed,
and comment-thread reads do not synchronize all replies. Memberships/partner operations are absent.
Durable refresh/revocation, credential generations with cross-store commit/recovery, consent issuer
mix-up defenses, and tracked retention/purge still require implementation. A revoked callback cannot
re-enable the database connection, but vault writes and database state do not yet share a durable
commit protocol; cleanup/reconciliation of that race remains required. In-flight old-format OAuth
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
