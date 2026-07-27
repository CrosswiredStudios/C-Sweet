# Phase 9A - Agent Broker and Personal Assistant

> **Superseded historical phase.** The transport and permission model below is retired. Use the
> [MCP-only migration](../../../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md) and
> [current architecture](../../../Documentation/Architecture/MCP_AGENT_RUNTIME.md).

## Goal

Introduce the first separately executable C-Sweet agent and the broker contract that future marketplace and GitHub-distributed agents will use.

The first agent is the official Personal Assistant / Chief of Staff. It must use the same registration, permissions, event routing, capability routing, and package manifest model expected of third-party agents. It must not receive direct database or RabbitMQ access through the agent protocol.

## Projects

| Project | Responsibility |
|---|---|
| `CSweet.Agent.Contracts` | Canonical Protobuf/gRPC contract and language-neutral package manifest. |
| `CSweet.Agent.SDK` | .NET convenience SDK, broker client, manifest loading, and agent runtime loop. |
| `CSweet.AgentHost` | Trusted gRPC broker endpoint, authorization policy, bounded logical sessions, event routing, and capability routing. |
| `CSweet.Agents.PersonalAssistant` | Separately executable official Personal Assistant agent. |

Other languages should generate clients from `agent_broker.proto` or implement the same protocol. They are not required to reference a C# assembly.

## Trust boundary

```text
Untrusted or semi-trusted agent process
    -> authenticated broker stream
CSweet.AgentHost
    -> validates configured grants
    -> stamps the session identity
    -> selects event recipients
    -> selects capability providers
    -> enforces business isolation
C-Sweet platform services
```

Agents do not:

- Connect directly to RabbitMQ.
- Select event recipients.
- Address another agent by installation identifier.
- Supply authoritative trust level or business identity after registration.
- Receive a capability request unless the broker grant includes that capability.
- Publish an event unless the broker grant includes that event type.

## Registration and grants

An agent package contains `csweet-agent.json`. The manifest declares requested capabilities, subscriptions, publications, permissions, and network access.

The manifest is a request, not authorization.

`CSweet.AgentHost` loads a separate administrator-controlled grant configuration. Registration receives only the intersection of the manifest request and the configured grant.

The first development configuration grants the official Personal Assistant:

- `assistant.converse.v1`
- `assistant.summarize-activity.v1`
- `assistant.plan-work.v1`
- User-message and approval-result subscriptions
- Assistant-response, progress, and action-proposal publications

The default policy rejects unknown and disabled agents.

## Event flow

```text
Platform publishes user.message.received
    -> broker validates publisher
    -> broker selects subscriptions in the same business
    -> Personal Assistant receives event
    -> Personal Assistant calls existing IAgentRunner
    -> Personal Assistant publishes assistant.response.created
    -> broker validates publication
    -> broker selects authorized subscribers
```

Event payloads should prefer resource identifiers over complete sensitive records. Receiving agents should request required fields through broker-controlled capabilities.

## Capability flow

```text
Agent requests capability by stable capability name
    -> broker finds an authorized provider in the same business
    -> broker records requester/provider correlation
    -> selected provider executes
    -> only the selected provider may return the result
    -> broker delivers result to the original requester
```

An agent requests `property.valuation.v1`, for example. It does not request a named marketplace agent. Later workforce routing can choose providers using trust, cost, performance, privacy, and company preferences.

## Personal Assistant behavior

The Personal Assistant:

- Uses the existing `IAgentRunner` and configured LLM provider profile.
- Treats documents, websites, tool output, worker output, and event payloads as untrusted data.
- Does not claim an action happened without a confirmed platform result.
- Proposes side effects instead of directly sending, purchasing, deleting, hiring, or publishing.
- Requests work by capability rather than contacting a named agent.
- Includes broker-stamped business context in model input.
- Publishes response events through the broker.

The first implementation returns an empty `ProposedActions` collection. Structured action extraction and approval integration should be added only after action schemas and policy executors are implemented.

## Local development

Run the Aspire AppHost:

```bash
dotnet run --project src/CSweet.AppHost/CSweet.AppHost.csproj
```

Aspire starts:

- PostgreSQL
- Migrator
- API
- Web application
- Worker host
- Agent host
- Personal Assistant

The Personal Assistant resolves the broker using `https+http://agenthost` through shared Aspire service discovery.

## Security implemented in this phase

- Deny-by-default configured agent grants.
- Business-scoped event and capability routing.
- Broker-selected recipients and providers.
- Bounded per-session outbound queues.
- Publication allowlists.
- Subscription allowlists.
- Capability allowlists.
- Correlated capability requests and results.
- Rejection of results from agents not selected by the broker.
- No direct RabbitMQ credentials in the agent SDK.
- No direct database contract in the agent SDK.
- Safety-focused Personal Assistant system prompt.

## Production hardening still required

This phase establishes the runtime contract but does not claim that arbitrary downloaded binaries are safe.

Before enabling community marketplace installation, implement:

1. Package checksum manifests and immutable artifact digests.
2. Sigstore/Cosign publisher-signature verification.
3. Separate C-Sweet review attestations and revocation status.
4. OCI and `.csagent` package installation.
5. Agent-folder discovery and atomic staged installation.
6. Container or WASI sandboxing for community agents.
7. Network egress allowlists and secret brokering.
8. Short-lived workload identity or mTLS for remote agent hosts.
9. Durable RabbitMQ-backed event delivery, retries, dead-lettering, and outbox/inbox persistence.
10. Rate limits by agent, publisher, business, trust tier, and event type.
11. Payload schema registration, validation, classification, and field-level redaction.
12. Signed marketplace metadata and secure update/rollback protection.
13. UI trust badges that distinguish signed, verified, reviewed, official, modified, and revoked packages.

A valid signature proves artifact identity and integrity. It does not by itself prove that an agent is safe.

## Tests

Unit coverage verifies:

- Requested permissions are reduced to the configured grant.
- Agents are rejected outside authorized businesses.
- Events do not cross business boundaries.
- Only subscribed sessions receive events.
- Capability requests route through the broker-selected provider.
- Capability results return to the requesting session.
- The Personal Assistant handles a user-message event and publishes a response event.

## Acceptance criteria

- [x] Personal Assistant is a separate executable project.
- [x] Package manifest is separate from runtime configuration.
- [x] Protobuf/gRPC is the canonical language-neutral runtime contract.
- [x] Agents register through a trusted broker.
- [x] Broker controls event delivery and capability provider selection.
- [x] Default grants are deny-by-default.
- [x] Business boundaries are enforced during routing.
- [x] Personal Assistant uses the existing LLM provider abstraction.
- [x] Personal Assistant proposes rather than directly executes side effects.
- [x] Aspire launches the broker and Personal Assistant.
- [ ] Community package signature verification is implemented.
- [ ] Community agents execute inside a hardened sandbox.
- [ ] Durable broker persistence is implemented.
# Superseded historical design

> This phase document records the retired gRPC agent runtime. The active design is [MCP Agent Runtime](../../../Documentation/Architecture/MCP_AGENT_RUNTIME.md) and must be used for current implementation decisions.
