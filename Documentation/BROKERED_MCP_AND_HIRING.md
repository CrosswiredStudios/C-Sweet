# Superseded: brokered MCP, structured questions, and hiring

> **Superseded historical design.** This phase document describes the former agent transport and
> must not be used for current implementation. See
> [MCP-only agent runtime](Architecture/MCP_AGENT_RUNTIME.md) and
> [the migration plan](Implementation/MCP_ONLY_AGENT_MIGRATION.md).

## Runtime contract

Agent containers remain language-neutral. They register over the existing gRPC broker for identity, lifecycle, events, and streaming. An accepted registration also returns a dedicated one-time-visible MCP bearer credential, endpoint, expiration, installation revision, granted requested-capability names, and separately identified global capabilities.

## Agent employee identity

An accepted registration for a hired agent also returns its organization-scoped employee identity: employee ID, hired display name, assigned role and responsibilities, authority level, and reporting manager. The package ID continues to identify the software implementation and is not an employee persona. Service installations that have not been hired do not receive an employee identity.

Before every broker-mediated LLM request, AgentHost reloads the active employee bound to the authenticated organization and installation and prepends a protected identity instruction. Matching employee or installation records in organization and workforce data are explicitly identified as self. Agent-authored role instructions are preserved after the platform block, but cannot replace the platform identity. Because resolution occurs per request, name, role, responsibility, authority, and manager changes take effect without restarting the agent. The registration field is additive, so older agents ignore it while still receiving broker-side identity enforcement.

The bearer credential is random and session-bound. C-Sweet stores only its SHA-256 hash after registration. It expires after one hour, stops working as soon as the broker session ends, and is not the container workload credential. Agents must reconnect to rotate it. Credentials belong in HTTP authorization headers, never prompts or tool arguments.

`POST /mcp` implements Streamable HTTP JSON-RPC initialization, tool discovery, and invocation. `tools/list` exposes global model-visible tools plus grant-required tools present in both the immutable installation grant and active session grant. `tools/call` rechecks the active organization and installation, current policy, request size, rate limit, basic JSON schema, and trusted capability handler. MCP never calls a handler outside the broker dispatcher.

Python agents can use the helper under `CSweetAgentSdk/python` with Microsoft Agent Framework's `MCPStreamableHTTPTool`. .NET agents can use `McpConnectionInfo` with an official MCP client, or the typed `PlatformCapabilityClient`/`PlatformToolAdapters`; both execution paths remain broker-authorized. Model-facing typed adapters receive the accepted registration grant and omit every capability that was not approved.

## Structured user questions

`ask_user` creates a durable structured question linked to organization, conversation, chat turn, requesting installation, and idempotency key. It accepts 2–4 mutually exclusive options and one recommended option. A new question supersedes the previous pending question for that agent conversation.

Communications renders the recommendation first, automatically supplies **Something else**, and requires Submit. Answers are immutable and idempotent. Submission stores the structured answer and starts the next agent turn. Failed or cancelled source turns cancel their pending question cards.

## Global tools

MCP descriptors classify discovery separately from execution risk: `GrantRequired`, `Global`, or `PlatformOnly`. A global tool does not need to appear in an agent package manifest or installation grant. It is still available only through a live authenticated broker session belonging to an enabled installation and active organization employee. Organization isolation, current installation state, schemas, rate limits, idempotency, and trusted handler checks continue to apply on every call.

The initial global catalog contains:

- `ask_user` (`platform.user-input.request.v1`) — creates one user-visible multiple-choice question in the current Communications conversation. It cannot install software, spend money, contact third parties, or perform the selected action.

Adding a global tool is a platform security change. The tool must be organization-scoped, safe for every agent, narrowly schema-bound, free of hidden external side effects, and implemented through the broker dispatcher. Global capabilities are declared by the trusted platform catalog and returned separately from installation-granted capabilities during registration.

## Hiring

`search_workforce` returns opaque, broker-resolved candidate references. Production searches current staff and installed resources; the synthetic marketplace provider is registered only when the host is in Development and `DevelopmentMarketplace:Enabled=true`.

`upsert_hiring_recommendation` maintains the ranked HR backlog. `stage_hiring_workflow` records an approval proposal but cannot install, spend, contact, or hire. The Employees **Hiring** tab shows one recommendation plus up to two alternatives. Only an organization owner can confirm. Confirmation revalidates organization membership, candidate availability, package digest, grants, financial controls, hiring cap, and budget before assigning current staff or hiring an already-installed agent. External installation and human-provider engagement remain disabled until trusted providers are implemented.
