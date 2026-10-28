# C-Sweet managed connection service implementation plan

Date: 2026-10-26. Status updated 2026-10-28: modular service and managed host consent/import,
refresh and primary revocation are implemented with protocol 0.3.0. General managed execution,
distributed cleanup and production acceptance remain incomplete. See [implementation progress](managed-connection-service-progress.md).

## Outcome

A customer installs a plugin, clicks **Connect account**, signs in on the provider's own website,
approves access, and returns to the plugin with their accessible resources imported. Ordinary users
never create a Google Cloud project or enter application credentials.

C-Sweet operates the shared application registration and a secure connection service. Plugins define
provider behavior. Self-hosted administrators may instead use customer-owned application credentials.
This extends the [enterprise account model](plugin-account-management-plan.md); existing business
grants, agent bindings, exact-action approvals and cleanup requirements remain independent controls.

Managed mode routes credentialed provider requests and media transfers through our service. It is
not just a login redirect: C-Sweet processes the authorized requests and responses. Disclose that
data path to customers. Customer-owned mode keeps those exchanges on their own deployment.

The companion [Google registration checklist](../deployment/google-youtube-connection-setup.md)
contains the operator tasks. Completing those tasks does not itself build or deploy this service.

## Customer experience

1. **Install.** Review the plugin's publisher, abilities and destinations, then approve installation.
   Broker-only connectors verify their declarations without a compiler, VM or agent schedule.
2. **Plugin page.** Show one prominent Connect account button, a short explanation of base access,
   and privacy/terms links. Put technical administration in a collapsed administrator-only section.
3. **Connect account.** The click records the human's acknowledgement of the displayed purpose and
   policy version and starts base authorization. Do not add a redundant permission wizard before
   the provider's consent screen. Optional publishing/management permissions have a separate action.
4. **Provider sign-in.** Navigate the top-level browser to the provider. The provider handles password,
   MFA, account selection and consent. C-Sweet does not embed or imitate that screen.
5. **Return and import.** Return to the originating plugin page, display import progress, then group
   verified resources beneath the account. Resume incomplete imports after a restart.
6. **Choose business access.** Offer selected resources or the whole authorized account. Nothing is
   granted to a business or agent merely because the account was connected.
7. **Enable more features later.** Explain the additional access and start new provider consent only
   after a human asks to enable the feature. Reconnection never restores disabled autonomous policies.

Show cancellation, denied access, missing channels, expired sign-in and service outages as specific
recoverable states. Preserve progress. Avoid repeated unchanged alerts and generic disabled buttons.
Known managed-service setup failures belong in operator diagnostics, not an ordinary customer's
Google-credentials form. Genuine security and provider failures must still stop authorization.

For YouTube, import what the authorization actually exposes. A Google account's ability to manage
many channels does not establish that one token exposes them all. Offer Connect another channel
when needed; partner-only discovery is not a shortcut for ordinary accounts.
[YouTube channel discovery](https://developers.google.com/youtube/data/reference/rest/v3/channels/list).

## Responsibilities

| Component | Owns |
| --- | --- |
| Connector plugin | Public provider metadata, scopes, permission labels, identity/resource discovery, typed request mappings, ownership checks, reconciliation and provider-specific lifecycle declarations |
| C-Sweet host | Installation review, native UI, human identity, business/agent grants, conversation approvals, durable local work, policy enforcement and local data cleanup |
| Managed connection service | Deployment enrollment, approved profile bindings, OAuth protocol, credential vault, remote connection state, bounded authenticated operations, quota controls and central cleanup |
| Google or another provider | Account sign-in, consent, provider-issued authorization and API eligibility |

The managed service has no LLM and loads no plugin executable code. Neither it nor the host contains
YouTube-specific account pages, operation switches or scope enums. Provider metadata lives in reviewed
plugin declarations and operator profile records. Platform-owned OAuth implementations and bounded
HTTP/media protocols are reusable mechanisms, not arbitrary provider application code.

## Deployment and credential modes

**Managed** is the default when the enterprise is enrolled with the connection service. The service
holds both application secrets and provider access/refresh tokens. Hosts and agents receive opaque,
deployment-bound connection references. There is no managed token-download endpoint.

**Customer owned** is an advanced self-hosted mode. Its administrator configures a local vault-backed
profile once. The current local OAuth/broker path remains available, with the same permission and
approval checks. This mode does not require the managed service for normal provider operations.

Choose the mode explicitly per connection profile. Do not silently fall back to local credentials
or export/import provider tokens between modes. Switching a connected account requires new consent
and explicit review of affected bindings; it does not transfer autonomous policies.

Use separate testing and production Google projects. Do not create customer/project pools to evade
quotas, and do not treat unrelated third-party plugins as automatically entitled to the C-Sweet
application identity. Confirm the API-client/product boundary, self-hosted use and commercial model
during provider review.
[Google environment requirements](https://developers.google.com/identity/protocols/oauth2/production-readiness/policy-compliance),
[YouTube credential policies](https://developers.google.com/youtube/terms/developer-policies).

## Service structure and protocol

Create a separate proposed repository, `CSweet.ConnectionService`, using .NET 10, a transactional
database, a managed secret store/KMS, and durable background jobs. Publish a versioned service API
and host client; do not turn the existing host into a provider application broker with hard-coded
integration logic. Infrastructure hosting and the actual service domain remain deployment decisions.

Reuse or extract the existing deterministic declaration validator, canonical request hashing,
resource checks, secret extraction and resumable-transfer mechanics. Keep one contract/conformance
suite for managed and local execution so implementations cannot disagree about an approved request.

Proposed v1 service operations:

- Register/revoke an enterprise deployment and rotate its authentication key.
- Resolve an approved package/profile binding and its connection availability.
- Start, inspect, cancel and complete a short-lived connection session.
- Read a sanitized connection/resource/permission projection.
- Execute an allowed bootstrap read or an exact prepared operation.
- Begin, resume, inspect and cancel a durable media transfer.
- Disconnect, inspect revocation/deletion progress and retrieve content-free receipts.
- Retrieve ordered events and current snapshots for recovery.

No endpoint accepts arbitrary provider URLs, arbitrary headers, user-supplied OAuth clients, raw
tokens, executable request expressions or an unverified `approved: true` flag. Connections are not
general-purpose authenticated HTTP proxies or a resale API for Google's services.

Keep existing agent-facing capabilities unchanged where possible. Connection mode and transport
selection belong below those APIs. Inspect the current SDK/protocol baseline before implementation;
use additive negotiated contracts and reject unsupported managed requirements. Bump/package SDK
contracts only if a public SDK change is actually necessary.

## Enrollment and package trust

1. Hosted deployments enroll using C-Sweet control-plane workload identity.
2. A self-hosted enterprise administrator pairs the instance with their C-Sweet service account once
   during platform setup. Bind its enterprise, deployment ID, public key and allowed return addresses.
   Ordinary account operators do not repeat this step.
3. Use per-deployment proof-of-possession credentials, short-lived service tokens, exact audiences,
   key rotation and revocation. Do not distribute a single service API key to all customers.
4. The service independently verifies the immutable connector declaration digest and its reviewed
   operations/destinations. Its operator registry explicitly allows the package/profile pairing.
5. The customer's administrator independently approves installation and consumers. A copied publisher
   name or profile ID conveys no access. Changed executable/semantic packages require new review.

Service operators provision profiles through protected administration. Plugins cannot register or
overwrite an application secret, change the service URL or redirect credentials to another provider.
Publish signed, expiring profile metadata and emergency revocation policy, not client secrets.

## Authorization and browser return

The host creates a durable attempt bound to the actual human, enterprise, deployment, installation,
package digest, connection declaration, selected scopes, policy version and registered return target.
Its authenticated service request obtains a short-lived, single-use navigation handle.

The service starts authorization-code OAuth with PKCE S256, state bound to the initiating browser
and attempt, exact redirect matching, expiry and replay protection. Register one fixed provider
callback per service environment, such as `https://connections.YOUR-DOMAIN/oauth/callback/google`.
Provider route keys are registry identifiers, not application-specific host code.

Provider codes and tokens terminate at the managed service. The browser returns to the host using
only a short-lived, single-use handoff reference. Redeem it through an authenticated back channel
with deployment/browser-bound proof; a connection ID alone is not authority. Recheck the initiating
host user/session and package before associating the result. Suppress codes and references from
logs, analytics, referrers and third-party scripts on callback/return pages.

Return targets must be pre-enrolled and exact, never a user-supplied redirect query. Hosted targets
use HTTPS. Supporting local development return targets requires an explicit development-only
loopback policy and the same proof checks; it must not relax production callbacks or enable SSRF.

## Credentials and connection records

Store enterprise/deployment registrations, immutable package/profile approvals, connection attempts,
provider authorization slots, confirmed subjects/resources, scope receipts, authority revisions,
operation receipts, media checkpoints, events and deletion obligations.

Encrypt provider credentials with envelope encryption and tenant-aware access checks. Separate public
OAuth client identity/profile revisions from secret-key versions: normal secret rotation must not
be confused with permission to switch to a different OAuth application. Existing
`PluginAccountProfileGuard` fingerprints include local secrets; managed mode needs a public binding
identity instead of copying that secret-bearing interface into the host.

Never merge accounts by email, display name or channel label. Preserve a separate authorization slot
for each proven resource relationship. Track actual provider-granted scopes and the narrower scopes
approved for each connection/consumer separately. A provider returning previously granted scopes
does not enlarge a tenant's permissions. Serialize refresh and credential generation changes.

## Operation and approval enforcement

Use the existing prepare, approve, execute boundary:

1. The host resolves current business/agent/resource authority and freezes a canonical plan.
2. The service independently materializes the request from its approved declaration snapshot, typed
   inputs and authenticated resource checks. Verify media digests and any approved previous results.
3. Bind authorization to deployment, tenant, consumer, connection/resource, profile/package revision,
   operation/effect, exact plan hash, decision/policy revision, expiry and idempotency key.
4. Recheck connection, policy and revocation state immediately before each outgoing request/chunk.
   Inject credentials only in the service transport. Reject modified paths, bodies or resource IDs.
5. Persist the result before announcing success. Reconcile uncertain effects before retrying;
   indeterminate uploads/replies remain visibly unresolved rather than being duplicated.

An enrolled self-hosted server is a customer trust boundary, not a remotely attested honest host.
Its signature alone cannot prove that a human approved an action or that a local database is truthful.
Require service-verifiable approver identity for explicit decisions, or an owner-approved standing
policy registered with the service. Requesting agents cannot approve their own actions. An agent
approver needs an explicitly delegated, service-verifiable identity and the same exact-plan binding;
the instance key is not proof of a human decision. High-risk action categories retain explicit
decisions even under autonomous policies. Show this trust model to account owners when they
authorize a self-hosted destination.

Use a restricted bootstrap permit for identity/discovery reads, not an ordinary mutation permit.
Keep provider consent, available API features, business access and individual consumer grants as
independent checks. Neither provider consent nor a connection reference overrides them.

## Media and secret responses

Hosts submit organization media-asset references and exact size/digest attestations, then stream
bounded chunks to service-owned durable transfers. Do not make the service fetch arbitrary customer
URLs or require public access to private media. Freeze asset identity and verify transferred bytes.
Retain upload locations and secret-bearing provider responses in the service vault; return opaque
references and sanitized progress. Explicit human secret-reveal controls remain separate and audited.

Persist restart/resume checkpoints, quota budgets and cancellation. Stream rather than buffering
whole videos; include bandwidth, storage, per-file and concurrency limits. Avoid permanent central
media copies. Include temporary buffers, processing logs and backups in the deletion inventory.

## Disconnect and durable recovery

Disconnection immediately fences local access and durably requests service fencing, cancellation,
provider revocation and deletion in both locations. The service applies its fence when it receives
the authenticated request. A network partition is not a revocation acknowledgement: show pending
central confirmation honestly and expire outstanding execution leases within a defined short bound
(at most 30 seconds in v1), rechecking before each request/chunk. Already sent effects cannot be undone.
Missing cleanup adapters cannot manufacture a completed receipt. A never-connected plugin removes
immediately; pending removal is not labeled Disabled.

Google revocation can affect the user's grants across clients in the same project. Model that
provider-declared revocation scope centrally, including sibling deployments, without exposing other
customers' identities. Warn accurately, invalidate affected authorizations and require reconnection.
Removing only a business grant is a distinct local access change, not provider disconnection.
[Google revocation behavior](https://developers.google.com/identity/protocols/oauth2/web-server#tokenrevoke).

Keep the existing 24-hour cleanup target and seven-day maximum for the YouTube flow, with applicable
metadata refresh/deletion deadlines. Track central, host, processor, memory and backup confirmations;
retain content-free security receipts. Review LLM/subprocessor disclosure and retention before moving
provider-derived text through those systems.
[YouTube data policies](https://developers.google.com/youtube/terms/developer-policies).

Publish durable signed events with unique IDs and monotonic per-connection revisions. Authenticate
webhook delivery when available; self-hosted instances can instead drain an outbound event feed.
Consumers deduplicate, ignore stale events and recover by current-state reads. Commit state and
outbox atomically. Service outages must fail closed for external work while conversation and recovery
assistance remain usable.

Operate per-provider, enterprise, deployment and account rate/concurrency budgets, circuit breakers,
bounded queues and quota-aware retries. Use reviewed operation costs, not caller-supplied discounts.
Monitor availability, consent failures, quota pressure and overdue cleanup without logging provider
content or secrets. Exercise key rotation, incident response and restore; restoring a backup must
not resurrect a revoked deployment, credential generation or autonomous policy.

## Changes to the current code

- `PluginConnectionOptions`: add managed service enrollment/endpoint configuration and explicit local
  mode selection. Managed configuration contains no provider client secret.
- `IPluginProviderProfileRegistry` and `PluginAccountProfileGuard`: separate sanitized availability/
  approved binding metadata from local secret-bearing credential resolution.
- `PluginAccountService.OAuth`: dispatch through a connection-backend interface to local or managed
  authorization; preserve durable account attempts and exact event correlation.
- `PluginAccountService.Discovery`, `.Bindings`, `.Management`, `.Disconnect` and `.Deletion`: consume
  sanitized remote results and coordinate both sides' authority and cleanup.
- `ConnectorHttpTransport` and `ConnectorMediaTransport`: select the local or managed operation
  transport below existing typed capabilities; never download managed tokens into the host.
- `ConnectorPlanService` and approval services: add service-verifiable grants/decisions and matching
  canonical plan/authority revisions rather than a second conflicting permission system.
- `Plugins`, `PluginManage` and native administration: default to the managed Connect action, move
  customer-owned configuration to Advanced, and distinguish provider outages from operator setup.
- YouTube package: retain provider metadata, progressive scopes, discovery and operations; publish
  updated policy links/declarations through the normal reviewed release flow where needed.

## Delivery sequence and acceptance

1. **Contracts and security design.** Freeze the v1 trust model, tenant/instance pairing, return
   protocol, approved profile registry, canonical operation envelope and local/managed conformance tests.
2. **Service foundation.** Implement enrollment, protected operator administration, vault/KMS,
   authorization attempts, database/outbox, health, quota controls, audit redaction and key rotation.
3. **Read-only vertical slice.** Install, Connect, real Google consent, return and resumable channel
   import. This is the first product acceptance milestone, not just a fake-provider test.
4. **Exact operations and media.** Implement independent service checks, decisions/policies,
   authenticated reads/mutations, uploads, reconciliation, cancellation and recovery.
5. **Lifecycle and optional local credentials.** Complete reconnect, broader revocation, deletion
   receipts, update review, mode switching, and customer-owned parity with no automatic token transfer.
6. **Production rollout.** Finish Google registration/review, security assessment as applicable,
   external penetration testing, capacity/incident/restore exercises and monitored pilot deployment.

Automated acceptance must cover cross-tenant/reference replay, fake publisher/profile claims,
scope escalation, unsafe redirects/DNS, changed plans/resources/media, stale decisions, self-hosted
key theft/revocation, partial consent, duplicate callbacks, refresh races, outage/restart recovery,
duplicate-effect prevention, disconnect during chunks, cleanup deadlines and redacted diagnostics.
Run the same workflow using a second fake provider without host/service application-specific code.

Browser acceptance must cover the complete normal journey plus cancellation, denial, an interrupted
return/import, progressive permissions, business access, approved reply/upload, disconnect and
plugin removal. Use isolated fake-provider tests plus explicitly authorized real Google accounts;
record which is which. Never call real consent, public uploading or verified deletion proven by mocks.

Public launch requires functioning provider credentials, correct registered service callbacks,
accepted policies and applicable provider review. Ordinary customers do not perform these tasks.
Registration can start before implementation, but the complete working feature flow is needed for
verification demonstrations and release acceptance.
