# Compute administration API

These control-plane APIs require the existing `HostAdministration` authorization policy. They are not agent capabilities. Apply the generated compute migrations before using them. For the current Linux test path and separately scoped command/port grants, see [Daniel Hello World](daniel-hello-world.md). Provider transport and dispatch are wired; installation and hardware acceptance remain operator steps.

All routes start with `/api/compute/administration/{organizationId}`:

| Method/path | Purpose |
| --- | --- |
| `PUT /nodes/{nodeId}` | Register or update an organization-bound provider node and its public verification key |
| `PUT /templates` | Approve an immutable template identity, or enable/disable an existing identity |
| `PUT /templates/{templateRegistrationId}/nodes/{nodeId}` | Enable/disable that approved template's placement on a node |
| `GET /signing-identity` | Read the configured control-plane signing key ID and public SPKI for provider pinning |
| `GET /nodes`, `GET /templates`, `GET /placements` | List up to 100 records; continue with `?afterId=<last returned ID>` |

Mutation bodies include `expectedRevision`: use zero to create and the returned revision to update. Stale revisions return conflict. Node bodies additionally contain `name`, `providerId`, `keyId`, `publicKey` (base64 DER SubjectPublicKeyInfo, P-256), and `enabled`. Never submit a private key. Organization and provider identity cannot be changed on an existing node. Rotate the public key with a new key ID; old signatures then fail current trust lookup.

Template bodies contain `template` with `id`, `operatingSystem`, `architecture`, canonical `sha256:` image digest, optional software `features` as a set, and `enabled`. Features may be empty for a clean operating system. Changing image contents or metadata requires a new template ID. The path's template registration ID is the returned database ID, not the template's human-readable ID. Placement bodies contain `expectedRevision` and `enabled`.

Disabling a **placement** removes it from new placement selection while preserving the node's result-verification trust. Disabling a **node** revokes that trust as well as excluding it from selection. Neither action deletes or migrates existing environments or releases their quota. Existing resources retain their provider/node identity until an explicit, fenced lifecycle workflow reconciles them. Disabling a **template** prevents new selection without changing its immutable image definition.

Registry mutations and audit-outbox evidence commit together. Audit evidence records the authenticated administrator supplied by the existing API middleware, revision, enabled state, and key fingerprint or template/placement references. Only public keys are stored. Provider-issued result signatures remain bound to the recorded operation, organization, installation, node, provider, specification and generation.

The registry approves placement availability; it does not prove that an image is installed or that capacity is available. Provider probing, image certification and physical resource accounting must be enforced during dispatch before this becomes an operational compute service.

## Dispatch signing

Configure `CSweet:ComputeSigning:CertificateThumbprint` and optionally `StoreLocation` (`CurrentUser` by default, or `LocalMachine`). The certificate must already be installed in that identity's `My` store, contain an accessible P-256 private signing key, and be within its validity period. The service does not create certificates, export private key material, or store it in compute records. Missing or invalid signing configuration fails closed. Pin the returned public signing identity at the privileged provider boundary through the enrollment workflow; retrieving the public key alone is not enrollment.

Dispatch claims last at most one minute and are bounded by current grant and environment lease validity for activation. The claim commits before a signed packet is returned. An active claim cannot be issued twice. After a possible dispatch and lost response, recovery packets have `Observe` mode and carry no agent mutation grants. Providers must enforce that mode independently. Transport, provider-side verification/fencing and certified-absence recovery are still required; these records alone do not authorize direct hypervisor calls from agents.

## Provider maintenance audit ingestion

The internal ComputeMaintenanceIngestor is registered in Core DI. It accepts only a distinct purpose-signed, bounded-lifetime maintenance delivery from a currently enrolled node. The event inside may be historical so offline queues can reconnect. Its bytes/digest, original provisioning operation and grant references, organization/installation, node/provider placement, generation, specification and lease/persistence terms must match recorded history. No new agent grant is issued or required for recording original cleanup evidence. Agent disable or current grant revocation does not erase historical attribution; revoked node trust still rejects delivery.

Valid evidence is appended directly to the existing sealed audit ledger under its immutable provider event ID. Re-signing an identical event after reconnect is idempotent. Changed evidence under an existing ID is rejected by ledger integrity checks. Rejections record fixed codes with unverified identity and omit untrusted payload metadata. Maintenance ingestion does not change lifecycle state, adopt a provider resource, release quota or certify storage teardown.

Signer, verifier, ingestion and lost-acknowledgement replay are tested together in process with the real sealed ledger. The HTTPS endpoint and provider client are implemented; deployed provider-service configuration and its delivery worker remain unwired. Future provider/lease migration must preserve immutable original placement and lease terms so historical evidence remains verifiable across those changes.
## Maintenance HTTPS delivery

Core maps POST /api/compute/providers/maintenance. Framework cookie/agent authentication is not authority for this route: the ingestor requires the enrolled node signature and original provisioning-history checks described above. Core must have completed normal first-run setup. Requests require HTTPS and application/json, reject content encoding, and are bounded to 256 KiB whether Content-Length is declared or transfer is chunked. A fixed-window policy permits 120 requests per source IP per minute, with no queue. Responses are not cacheable.

A successful 200 response contains eventId and eventDigest only after the sealed ledger append completes. Invalid JSON, authority, media/encoding, size and rate return errors; ledger conflicts/unavailability do not acknowledge delivery. The provider retains queued evidence for every non-200 result, network failure or unexpected acknowledgement.

ComputeMaintenanceHttpClient takes a service-configured HTTPS origin and derives the fixed route. It uses normal TLS validation, no cookies or default credentials, and disables redirects. The complete delivery has a 30-second deadline. It reads at most 4 KiB of acknowledgement and requires the exact queued event ID/digest. No request supplies host paths or provider credentials.

Loopback HTTP/HTTPS tests cover actual ledger delivery, cookie exclusion, redirect refusal, mismatched/oversized acknowledgements, invalid requests, declared/chunked body limits and rate limiting. On Windows they require an unsandboxed test process for Schannel, create unique named CNG test keys, and explicitly delete/check those keys at teardown. Test certificates are pinned only by the test handler and are never installed as trusted certificates. Production TLS validation is unchanged. Windows ephemeral-key behavior is documented in the [.NET runtime discussion](https://github.com/dotnet/runtime/issues/23749).
## Broker access audit

ComputeBroker.ReadAsync and ListAsync now append `compute.access.v1` to the sealed ledger before returning an authorized response. Evidence records the verified installation, organization/workstream, action, one sufficient current scoped grant and revision, and the bounded returned environment IDs/revisions/generations. It excludes caller idempotency keys, provider resource identity and specifications. If ledger append fails, the response is not returned; compute state is unchanged.

Authorization and pagination rejections append `compute.access.rejected.v1` with an unverified caller, requested organization and fixed reason/action. They remain in the system stream without an entity ID or details from the requested environment. Each read attempt has its own audit event; this is disclosure evidence, not a lifecycle operation or execution grant. The authenticated MCP handler and tool catalog are now registered; typed SDK helpers and user-facing compute administration remain separate work.
## Authenticated agent compute tools

The existing MCP gateway/catalog/PlatformCapabilityDispatcher now routes seven granted capabilities through ComputeCapabilityHandler into IComputeBroker. Organization and installation come only from AgentSession. RequestingAgentId and payload fields do not override them. The session must contain the requested capability, and the broker rechecks current installation/workstream/scoped grants. Closed input schemas reject caller identity, host paths and lifecycle action overrides; the tool capability selects the action. Inputs are bounded to 32 KiB, list pages to 100. Errors use fixed codes without backend exception text.

| Capability | Tool |
| --- | --- |
| Compute.Provision | request_compute_environment |
| Compute.Read | read_compute_environment |
| Compute.List | list_compute_environments |
| Compute.Start | start_compute_environment |
| Compute.Stop | stop_compute_environment |
| Compute.Restart | restart_compute_environment |
| Compute.Destroy | destroy_compute_environment |

Provision input includes workstreamId, desiredEnvironmentKey, idempotencyKey and specification (operatingSystem, architecture, templateId, resources, lifetimeSeconds, optional persistence/network). Lifecycle input includes environmentId, expectedGeneration and idempotencyKey, without an action field. Read accepts environmentId; list accepts workstreamId with optional afterId/limit. Manifests still need approved requirements/model visibility and current scoped infrastructure grants; registration does not grant new authority to installed agents. Network/persistence remain separately authorized. Responses report durable desired/observed state, not physical readiness. Provider dispatch/result transport remains unfinished, so this endpoint registration is not operational provisioning acceptance.
Authenticated-session integration tests issue real test workload sessions and exercise the gateway's authentication/catalog/schema/dispatcher sequence in process. An installation grant revision change or disabled installation invalidates its existing token before compute dispatch. Revoking a scoped compute grant also denies provisioning when the session credential itself is still valid. These tests do not substitute for full HTTP gateway or physical provider acceptance.
### Provider lifecycle results

`POST /api/compute/providers/results` accepts an enrolled node's purpose-signed lifecycle observation over HTTPS. The body limit is 256 KiB; content encoding is rejected. Node trust, operation identity, generation, sequence, state and observation expiry are checked by the existing reconciler. A successful acknowledgement identifies the operation, sequence and SHA-256 payload digest; `applied: false` can acknowledge an unchanged or superseded result and is not a claim that the requested physical state was reached. Teardown releases Core reservations only when the reconciler accepts a confirmed destroy result. Maintenance evidence remains a separate ledger-only protocol.

The runtime signer preserves the original observation timestamps and sequence. Expired evidence requires a new physical observation, not a refreshed signature timestamp. Provider-side durable result delivery and executor integration remain pending; this endpoint alone does not make the provider operational.

### Provider work discovery and claims

Enrolled providers can POST a purpose-signed short-lived work request to `/api/compute/providers/work` for up to 100 due operation IDs on their own placement. Use the returned operation-ID cursor for bounded recovery. Discovery records a read audit and does not grant execution. POST a signed single-operation request to `/api/compute/providers/claims` for a Core-signed dispatch packet; HTTP 204 means no claim is currently available. Existing dispatch leases deduplicate concurrent/replayed requests, and an attempted operation is recovered with observation-only authority.

Both routes require HTTPS, strict JSON without content encoding, a body no larger than 64 KiB and the shared provider ingress rate limit. Cookies and agent session identity do not grant node authority. Provider client/reconnect composition and real-network acceptance remain pending.

`POST /api/compute/providers/result-receipts` accepts a fresh signed node work proof naming an exact operation, result sequence and payload digest. A matching committed receipt returns HTTP 200 with `Applied = false`; no matching receipt returns 204. This can recover an acknowledgement after the original observation expires. It confirms historical receipt only, never current physical state, teardown authority or permission to discard other evidence. The provider client validates all acknowledgement fields. Automatic host recovery integration remains pending.

Receipt responses explicitly identify `recorded` versus `superseded` disposition. Superseded means Core authorizes retiring that exact pending evidence because its operation/generation is no longer current; it does not mean the reported physical state was applied. The disposition is audited before response. Neither disposition permits VM/storage deletion or releases a reservation, and conflicting evidence at an already recorded sequence remains unresolved.
