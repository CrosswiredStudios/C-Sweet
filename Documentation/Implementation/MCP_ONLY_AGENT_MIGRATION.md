# MCP-only agent migration

Status: coordinated breaking cutover plan and checklist.

## Repository matrix

| Repository/project | Required outcome |
|---|---|
| `csweet` | MCP gateway, sessions, registry, durable inbox, authorization, all platform delivery paths, persistence, containers, tests, docs; delete agent gRPC |
| `CSweetAgentSdk` | SDK 1.0 transport-neutral API and private MCP runtime |
| `CSweetAgentChiefOfStaff` (`CSweet.Agent.ChiefOfStaff` in the catalog) | SDK 1.0 callbacks, live tools, durable progress/results, v2 manifest/tests/docs |
| `CSweet.Agent.ProductManager` | same migration as Chief of Staff |
| `CSweet.Plugins.Communication.Discord` | shared agent/service runtime and brokered HTTP/WebSocket capabilities |
| `CSweet.Memory` / `CSweet.Memory.Broker` | typed SDK platform memory client; no agent transport client |
| `CSweet.Market` | validate v2 schemas/hashes/risk/runtime compatibility; reject legacy executables |
| Software Architect, Software Developer, Software QA | `Planned`, non-installable; first implementation must start on SDK 1.0/v2 |

Unknown executable packages whose protocol maximum is below 2.0 are incompatible. Preview may explain migration, but import approval, install, update approval, and startup reject them. There is no enabled compatibility bridge.

## Ordered workstreams

1. Freeze the architecture, threat model, v2 schema, protocol, migration guide, and runbook.
2. Land registry/schema validation, sessions, inbox, authorization, bindings, persistence, and private listener.
3. Publish SDK 1.0 with its in-memory fake runtime and documentation tests.
4. Update Memory and Market.
5. Migrate Chief of Staff, Product Manager, and Discord.
6. Rewire chat, onboarding, configuration, management, scheduling, communication, and agent coordination.
7. Pass security, fault, load, end-to-end, and documentation gates.
8. Quiesce runtimes, migrate data, deploy compatible packages, disable legacy installations, and restart.
9. Delete protobuf/gRPC endpoints, packages, generated code, configuration, permissions/publications, and obsolete tests in the same release.
10. Enable production alerts before marking v2 agents installable.

## Database and cutover

Back up and verify restore. Quiesce new work and wait for/cancel active leases. Create session, work, attempt, progress, and capability-binding tables. Backfill v2 grant fields from approved data only, assign grant revisions, and encrypt queued payloads/results. Do not infer provider bindings when multiple providers exist. Disable any installation without a v2 package, valid descriptor hashes, complete grant, and unambiguous bindings.

Deploy the database migration, gateway/runtime manager, SDK-compatible packages, and API workers as one coordinated release. Revoke old sessions, restart compatible installations, and run smoke tests before reopening installs. Rollback restores the prior application/database backup with all executable installations disabled; it never enables gRPC.

## First-party checklist

- SDK 1.0 package/project reference and `AddCSweetAgent<TAgent>()`.
- Only `AgentCapabilityRequest`, `AgentEventEnvelope`, `AgentWorkResult`, `AgentRuntimeContext.Platform`, progress, and live model tools.
- Manifest v2 with provided descriptions, schemas, timeout, idempotency, protocol 2.x, requested authority, subscriptions, and network rules.
- No generic publications or caller-selected installation.
- README, grants, migration note, capability contracts, side effects, progress, approvals, idempotency, tests.
- In-memory fake runtime tests and end-to-end durable-work test.

## Test gates and definition of done

Required suites cover session rotation/replay/revocation/restart/token leaks; grant/binding/tenant matrices; schema fuzzing; claim/lease/crash/idempotency/dead-letter/database restart; malicious-container reachability; SSRF/rebinding/redirect/credential origin/WebSocket revocation; queue/progress/LLM/long-poll pressure; malicious outputs/prompt injection/audit leakage; all first-party product flows; marketplace v2/legacy rejection; and documentation links/snippets/manifests/generated grants/stale wording.

Done means no executable agent/service references gRPC, protobuf, `IAgentBrokerClient`, `context.Broker`, `BrokerLlmClient`, or `PlatformToolAdapters`; no container listener or inspectable transport credential exists; no compatibility endpoint or permission/publication bypass remains; discovery and calls match current grants/bindings; durable work survives restarts; first-party workflows use only SDK abstractions; planned catalog entries cannot install.

## Removal scan

The release gate scans current source/configuration/docs for protobuf files, gRPC packages/services/listeners, legacy transport types, workload-token environment variables, broker endpoints, permission/publication columns, synthetic gateway agents, direct model fallback, and active gRPC prose. Historical documents may retain context only with a prominent superseded banner linking to this document.

