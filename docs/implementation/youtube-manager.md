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
- Both new repositories scaffolded; production agent/connector behaviors are not implemented yet.

The user approved targeting the shared SDK **3.30.0** release. Concurrent Git file-lock
changes are preserved. Verification: 124 SDK tests, 2 sample tests, 7 generated-template
tests and template self-test passed; selected host tests passed 76/76. The memory suite
passed 20/20. SDK 3.30.0 and Memory.Broker 0.1.3 were packed locally and their NuGet
metadata inspected. The adapter's stale SDK 1.0.2 pin was corrected; host memory pins
now match the adapter's 0.1.2 core dependency. AgentHost builds with **all external sibling
project references disabled**, using a fresh package cache and the local verification feed
(zero warnings/errors). Nothing was published. These are foundation tests, not end-to-end
YouTube acceptance or verification of the still-template agent/connector repositories.

The complete implementation still requires mutation approvals/execution and media jobs;
complete setup/CEO handoff and bundle UI; the actual YouTube operation catalog and
conversational agent; OAuth/retention/recovery hardening; and full product acceptance.

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
