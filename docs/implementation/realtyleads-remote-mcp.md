# RealtyLeadsAI reviewed remote MCP

Status: implemented and verified locally on `feature/realtyleads-api-key-mcp`.
Unpublished; no deployment, real credentials, production grants, running containers,
websites or RealtyLeadsAI source were changed. Coordinate host and SDK 5.6.0 rollout
before importing the new plugin and agent into a real environment. Reviewed provider
imports require immutable repository sources; source archives and executable bundles
are rejected. See `realtyleads-development-rollout.md` for the proposed isolated rollout
and `realtyleads-security-review.md` for the final security review.

## Native account and execution path

- `RemoteMcpAccountEndpoints`, `RemoteMcpAccountApiClient` and `RemoteMcpAccounts.razor`
  provide authenticated native key entry, explicit consumer consent, rotation and
  revocation. Mutations require a custom native header; key bodies are bounded and
  parsed without reflection-based body binding. Password input is write-only and
  cleared on submission. Responses and errors never return the key.
- `RemoteMcpAccountService` requires an active human owner with current Manager or
  higher membership in the selected business. This is the first-release access
  policy. Other managers cannot inspect or operate that owner's connections.
  Scope is owner + business + provider installation + opaque connection ID; the
  current C-Sweet domain has no separate workspace identity.
- `AccountVault` protects keys and grants with scope-specific Data Protection
  purposes. `RemoteMcpAccountRepository` persists ciphertext in existing
  `PluginOperationalState` records with revision compare-and-swap. No migration
  is required. Application Data Protection key-ring security remains a host concern.
- Connection creation stores an unverified key and grants nothing. Explicit human
  approval binds one connection to one consumer and both read capabilities. Current
  business membership, provider digest, consumer grants, dependency and account
  generation are rechecked at dispatch and before result release. Workloads cannot
  choose keys, accounts, owners, endpoints, headers or generations.
- Rotation clears grants. Rotation/revocation/rebinding revoke affected agent
  sessions, cancel pending and leased work, and fence in-flight broker results.
  Installation removal deletes the associated encrypted operational records.
- `ConnectorBindingService`, `McpToolCatalog` and `McpGatewayEndpoints` resolve this
  distinct host mode without OAuth refresh or generic web-proxy injection. The
  existing OAuth connector validator remains intact.

## Reviewed transport and manifests

`RemoteMcpBroker` injects `X-Api-Key` only at the fixed
`https://realtyleads.ai/mcp` endpoint. It disables redirects, cookies and proxies,
resolves/pins public IPv4 addresses with TLS validation, and applies a 30-second
budget, 1 MiB response bound and JSON depth limits. It performs stateless MCP
initialize/list/call with protocol 2025-03-26. Only the two reviewed tool names and
parameter types are accepted. `RealtyPolicy` enforces closed arguments, bounded
strings, limit 1–100, score 0–100 and UUID lookup. Results contain only eleven
reviewed lead fields; remote descriptions, headers and raw errors are withheld.
Raw, JSON-decoded, URI-encoded and Base64 key echoes cause withholding.

SDK `ReviewedRemoteMcpManifest` and `ReviewedRemoteMcpValidator` define manifest
2.0, kind `remote-mcp`, runtime `host-remote-mcp`, minimum protocol 2.5 and the fixed
`realtyleads.read.v1` profile. `PluginManifestReader` and `AgentImportPreviewService`
accept only that closed profile. `AgentBuildService` verifies immutable source and
manifest/capability digests; this provider uses the host-only connector installation
path and creates no workload. Older hosts reject the new kind/protocol. No Office
runtime change is required. The separate typed `RealtyLeadsAgent` invokes only the
two granted business capabilities and has no network or credential access.

## Intentional limits

The provider's company identity is not discovered or verified by C-Sweet and must
not be equated with a C-Sweet business ID. RealtyLeadsAI enforces account/company
membership and role eligibility. Invalid provider keys are detected on the first
read, not at connection creation. No live authenticated provider test was performed.

This first release rejects stateful sessions, replay events, multiple SSE messages,
IPv6-only DNS answers, paged tool discovery, changed tools/schemas and non-JSON lead
payloads. No-subscription/not-found text is withheld as a generic connection error.
Each connection grants one consumer at a time; changing it requires explicit
native approval and stops affected current/queued agent work.

## Verification

- Full SDK suite: 609 SDK tests and 2 sample tests pass.
- Template temporary-repository tests: 7 pass; generated executable self-test passes.
- Agent: 6 tests using SDK package references and fake capabilities.
- Core targeted import/install/account/binding/catalog/OAuth regression suite: 182 pass after final review.
- Broker synthetic security suite: 29 pass, including isolation, generation fencing,
  invalid keys/401, redirects, echoes, session rejection, JSON/SSE bounds and injection.
- Native browser acceptance: production page/endpoints and actual agent callbacks
  with fake MCP complete connect → explicit grant → read → revoke → denied.
  Also checks owner rejection, missing native header, body size and no key readback.
  See `tests/CSweet.RemoteMcp.AcceptanceHarness` for reproducible instructions.
  This uses an SDK test runtime, not a deployed workload VM.
- SDK 5.6.0 is packed locally; downstream package-only validation uses an isolated
  cache to avoid stale unpublished packages. No source-reference-only release claim.
- Full core unit/integration/workflow regression results are recorded separately in
  `realtyleads-regression-review.md`. No live provider authenticated call, package publishing
  or deployment. Existing core nullable/analyzer/NuGet advisory warnings remain.

On this Mac, use a physical TMPDIR for SDK/template checks: the default `/var`
alias causes existing workspace-path tests to fail without any feature changes.

## Next secure test

After separate authorization for a development rollout, install the matching host,
SDK package, plugin and agent. A human enters their authorized subscription-scoped
RealtyLeadsAI key directly in native plugin account settings, explicitly approves
the intended agent/business, runs one bounded search and lookup, then revokes and
verifies denial. Never transmit the real key via chat, commands, agent arguments,
repository files or environment variables.
