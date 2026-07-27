# Agent runtime threat model

Status: required security review for protocol v2.

## Security premise

Installed agent and service code is potentially hostile. Prompts, model output, tool arguments, provider responses, work payloads, progress, and results are untrusted. The security objective is to limit a compromised agent to the precise authority intentionally granted to its current installation and to make abuse observable and recoverable.

Assets include tenant data, credentials, money and quota, approval state, platform integrity, audit evidence, other agents, host/container infrastructure, and service availability. Actors include honest and malicious publishers, compromised dependencies, prompt-injected models, organization users, external services, and platform operators.

## Threat controls

| Threat | Preventive controls | Detective controls | Recovery |
|---|---|---|---|
| Credential theft/replay | Secret-file bootstrap; token hashes; runtime/package/grant binding; 10-minute session; rotation; fixed-time checks | replay, expired-token, wrong-runtime alerts | revoke sessions, rotate credentials, terminate runtime |
| Cross-tenant access | server-resolved organization/resource identity; exact-installation work; same-org bindings; ownership checks | cross-tenant denial counter and high-severity alert | disable installation, preserve audit, investigate affected records |
| Confused deputy | explicit descriptors, scope resolvers, approval/budget policy, credential-origin binding, no caller-selected provider | capability-specific denial/audit reasons | revoke grant/binding; invalidate approvals |
| SSRF and exfiltration | public-address validation, DNS pinning, blocked CIDRs, normalized origins, exact path boundaries, redirect reauthorization, body limits, connection limits | DNS/rebind/redirect/private-address alerts and destination metrics | revoke network grants/credentials; stop containers |
| Prompt injection | treat model and retrieved text as data; tools remain grant- and schema-bound; approvals for material effects | unusual tool/denial sequences and output validation failures | cancel work, revoke grant, quarantine source |
| Queue/lease abuse | exact installation queues, depth/concurrency quotas, 25-second bounded poll, 60-second hashed leases, bounded retry | queue depth, lease churn, stale/forged lease, dead-letter alerts | cancel/requeue/dead-letter; disable installation |
| Result forgery | lease/runtime binding, monotonic progress, output schema, size limits, attempt/hash idempotency | conflicting completion and invalid-output alerts | reject result, retry or dead-letter |
| Denial of service | CPU/memory/PID/runtime limits; distributed quotas; bounded input/output/progress; connection caps | saturation, long-poll, LLM/cost, question-spam dashboards | throttle, cancel, terminate, disable |
| Supply-chain compromise | digest/version binding, signed/approved revisions, descriptor hashes, reapproval on code/schema/destination/credential/grant changes | digest mismatch and marketplace provenance alerts | quarantine revision, revoke sessions, rollback |
| Container escape | non-root, read-only package, dropped capabilities, no-new-privileges, no host mounts/socket/ports, isolated internal network | runtime anomaly and forbidden-network tests | terminate, isolate host, rotate secrets, incident response |

Audit records use capability-specific allowlists and hashes. They must never preview raw tokens, credentials, arbitrary prompts, response bodies, or encrypted inbox data.

## Residual risk

An agent can intentionally and completely abuse every capability that was granted to it, within enforced quotas and approvals. MCP is not a sandbox and discovery is not authorization. The container boundary, live authorization pipeline, narrow schemas/scopes, destination policy, approval gates, and monitoring are all required.

## Capability security review

Before adding or changing a capability:

- Identify owner, caller, assets, side effects, risk class, quota class, timeout, and model visibility.
- Specify restrictive input/output schemas, including nested `additionalProperties`, formats, lengths, counts, ranges, and depth.
- Resolve tenant, installation, user, resource ownership, and destination server-side.
- Define idempotency, retry, cancellation, approval, budget, and failure behavior.
- Review all URLs, redirects, DNS behavior, credentials, origins, paths, WebSockets, and response limits.
- Define safe audit fields, alerts, dashboards, and recovery steps.
- Require a new approval if code, descriptor/schema, requested grant, destination, or credential changes.
- Add negative tests for hidden calls, stale revisions, cross-tenant IDs, malformed/deep payloads, replay, flooding, malicious output, and secret leakage.

