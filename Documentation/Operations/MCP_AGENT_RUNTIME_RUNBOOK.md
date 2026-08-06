# MCP agent runtime runbook

Status: production operations for protocol v2.

## Dashboards and alerts

Track active/failed guest handshakes, session establishment, renewal age, revocation and replay; queue depth/age by organization and installation; claim latency; active/stale leases; attempt/retry/dead-letter counts; progress rate/size; completion latency/conflicts; VM/provider health and certification expiry; capability denial/schema/output errors; per-capability quota/cost; brokered HTTP/WebSocket destinations and denials; LLM budget; and credential use.

Initial alert thresholds:

- page on token replay, any cross-tenant attempt, guest escape/forbidden network reachability, provider certification failure, credential-origin mismatch, or sustained session-init failure above 10% for five minutes;
- page when oldest available work exceeds five minutes for an always-on installation, dead letters exceed three in fifteen minutes, or active leases remain stale for two lease periods;
- warn at 70% and page at 90% of organization/install/capability cost, concurrency, queue, or connection quota;
- warn on ten schema/authorization denials per installation in five minutes, progress above 60/minute per work item, or long-poll concurrency above 80% capacity.

Tune thresholds from production baselines without weakening hard enforcement.

Current gateway hard limits are 240 requests per session/IP per sliding minute with no queued
rate-limit requests, 1 MiB per MCP request, 1,000 pending/leased items per installation, 256 KiB
per work payload, 64 KiB and 1,000 records of progress per work item, and 1 MiB per completion.
The work lease is 60 seconds and the SDK renews every 20 seconds. Operators may lower these limits;
raising them requires a capacity and abuse review.

## Common operations

- Token exposure: revoke all sessions for runtime/installation, destroy the VM and writable disk, rotate workload and affected service credentials, preserve audit, then restart only approved guest-image and artifact digests.
- Disable installation: set installation/schedule disabled, revoke sessions and bindings, cancel pending work, terminate every runtime.
- Cancel work: mark pending/leased work cancelled; the SDK observes cancellation and the platform terminates after grace.
- Retry work: only requeue a retry-safe/dead-letter item after reviewing its effect/idempotency key; create a new attempt, never rewrite history.
- Dead-letter recovery: inspect sanitized error, package/grant revision, attempts, and owning service; correct the cause; requeue or close with an owner-visible explanation.
- VM workload termination: revoke first, request advisory shutdown if safe, then force terminate at the deadline and destroy the ephemeral writable disk. Preserve only bounded logs and audit evidence.

## Incidents

Suspected malicious agent: disable installation, revoke sessions/bindings, cancel work, destroy its VMs and writable disks, freeze package/grant/audit evidence, search for related destination/credential/tool activity, rotate exposed credentials, assess tenant effects, and require a new approved revision.

Credential exposure: revoke sessions and the credential at its owning provider, enumerate authorized origin calls, invalidate pending approvals that depended on it, rotate, and reauthorize destinations.

Cross-tenant attempt: treat as high severity. Preserve the rejected request hash and server-resolved identities, disable the installation, audit adjacent calls and data, validate no effect occurred, and review the scope resolver.

SSRF/rebinding: revoke network grants and credentials, terminate the runtime, capture normalized URL/DNS/redirect evidence, block the CIDR/origin, and test the bypass before restoring.

Queue flooding: throttle the installation/organization, stop new enqueue sources, preserve representative items, cancel duplicates, terminate a malicious runtime, then drain within quotas.

## Deployment and recovery

Before deploy: backup/restore-test the database, verify package and guest-image digests and grants/bindings, run migration and provider certification gates, confirm RuntimeHost local-RPC authentication and broker-only transports, and quiesce legacy runtimes.

Deploy database, gateway, API workers, runtime manager, and compatible packages together. Revoke pre-deploy sessions, restart exact installations, verify initialize/renew/claim/progress/complete, test denied hidden tools and cross-tenant IDs, and watch dashboards for one full lease/retry window.

Rollback stops all runtimes, revokes sessions, restores the compatible application/database state, and leaves executable installations disabled until validated. Never restore or expose an agent gRPC endpoint.
