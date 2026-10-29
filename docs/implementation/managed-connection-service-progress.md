# Managed connection service implementation progress

Updated 28 October 2026 Pacific. The sibling `CSweet.ConnectionService` repository implements
managed account consent/import and native host integration using protocol package **0.3.0**.
The protocol is packed locally but not published. The conversational agent is unchanged.
General managed provider operations and production acceptance are not complete.

## Separation and implemented paths

- `CSweet.ConnectionService.Core.ConnectionModules` registers statically trusted connection modules.
  A non-OAuth test module proves the extension seam; no mobile-link feature is implemented.
- `CSweet.ConnectionService.OAuth` owns provider-neutral consent, reviewed package/profile registry,
  encrypted credential custody, deployment proofs, inventory, refresh, revocation and durable events.
- `CSweet.ConnectionService.OAuth.Google.GoogleOAuthCredentialDocumentReader` validates Google's
  downloaded web-client format, normalizes its legacy exported authorization endpoint and binds the
  exact central callback/project revocation group. It does not define YouTube operations.
- Reviewed plugin manifests define provider metadata, scope sets, identity/resource reads and native
  projections. No provider conditionals, plugin executables or LLM are loaded by the generic service.

`ManagedPluginConnectionGateway` stores a per-deployment proof key protected by host Data Protection.
`PreparePairing` returns public details only; `CompletePairing` verifies signed possession against the
actual service registration before enabling the binding. The administrator page is
`ManagedConnections` at `/settings/connections`. Enrollment still requires an authorized central
OIDC token; full interactive control-plane sign-in/enrollment and key-rotation UI remain unfinished.

`PluginAccountService.BeginManaged`, `CompleteManagedAsync` and `ImportManaged` connect native setup
records to central consent. The return is bound to the initiating human, HttpOnly browser cookie,
installation, reviewed digest, exact return address and return-verifier challenge. The browser removes
the opaque fragment before sending it to authenticated native return handling. A lost redemption
response has proof-bound receipt recovery; the native page supports an in-memory retry.

`ConnectionDiscoveryService` permits only reviewed bootstrap identity/resource GET operations,
validates inputs and outputs, pins public destinations and persists encrypted bounded pagination.
Provider-specific scope aliases/order live in the Google module, not generic OAuth or the host.
It returns sanitized identity/resources, never provider tokens. `FinishImport` reuses native account
deduplication and explicit business/resource grants. Connection does not automatically grant business
or agent access. Local/managed schema checks share the protocol's bounded validator.

`ConnectionCredentialService` serializes refresh with durable version/lease fences and rejects missing
required permissions. Additional provider token scopes never become new reviewed scope sets.
Uncertain refresh outcomes require reconnection instead of replaying a potentially
rotated token. `ConsentService.CancelAttempt` cancels using the original local identity even if a
begin response was lost; a tombstone prevents late initiation. `Supersede` requires a fresh verified
same-profile/installation/subject replacement before retiring old service credentials without
revoking the new provider grant. Native retirement and abandoned-attempt processing are durable.

`ConnectionRevocationService` calls the real transport's provider revoke implementation and erases
primary secrets only after confirmation. Independent provider/backup/processor copies are not
implicitly declared deleted. Native disconnect records `external:managed-service-backups` separately.
`Revoked` is not universal erasure evidence.

## Configuration and user experience

`PluginConnectionOptions.Managed` explicitly selects managed mode. It specifies a trusted service
origin, exact native return URI, protected registration path and optional `CustomerOwnedProfiles`.
Managed outages never fall back to local application secrets. The local token broker also rejects
managed authority and old local tokens after a managed-mode switch; switching back requires new consent.
Already recorded managed metadata remains ineligible for local token use.

Changing the mode for future connections does not clear an existing managed cleanup obligation.
Retirement uses the protected context of the old connection: a managed replacement must be verified
centrally for supersession; a customer-owned replacement instead revokes the original managed attempt
and waits for its matching `Revoked` snapshot. Unavailable or malformed cleanup evidence remains
pending without stopping independent imports. The paired gateway permits signed cleanup after
managed mode is disabled, while new managed access stays disabled. Keep its protected registration
and exact service configuration until cleanup completes. Provider-wide revocation can require fresh
customer-owned consent; a local replacement is never claimed as a service-verified supersession.

The plugin catalog/manage screens do not ask for local provider secrets in managed mode. Explicit
customer-owned declarations retain their administrator-only local configuration. Account owners
connect through the focused plugin page; pairing and diagnostics stay administrative. Managed mode is
disabled by default until an actual trusted service has been configured and paired.

Host configuration names and the exact Google callback are in the service repository's
`docs/hosting.md`. `docs/hosting-mac-studio.md` describes the proposed
`connections.c-sweet.com` Mac Studio/Cloudflare deployment. DNS, public hosting, Google credentials,
production data and the running development app have not been changed.

## Verification and release gaps

The checked-in service tests exercise HTTP endpoints, SQLite, actual cryptographic deployment proofs,
persistent test key rings, fake Google-shaped and alternate-provider responses, and a non-OAuth module.
Host tests exercise native managed setup/import/disconnect with a deterministic gateway, plus real
gateway signing, protected registration storage, pairing validation and focused UI rendering.
The existing browser harness exercises native pages with a fake local provider; it is not an
integrated service-to-host browser or Google acceptance test.

Latest local verification: **80 service tests**, **146 focused host tests**, the synthetic native-page
browser story and real return-fragment tampering checks pass. Service publish and a fresh NuGet-only
protocol consumer pass. The host build still reports pre-existing vulnerable SignalR/cryptography
package and nullable/analyzer warnings; these are not cleared by connection-service acceptance.

Before release:

1. Publish the reviewed **0.3.0** protocol package; downstream verification uses the packed package,
   with SDK/WorkManagement/Office sibling source references disabled, not an unpublished-source shortcut.
2. Implement ordinary managed reads/writes, service-verifiable approver identity/owner policies,
   independent exact-plan/resource enforcement, media transfer, secret extraction and reconciliation.
   A deployment signature does not prove a human's mutation decision. These routes remain closed.
3. Complete interactive central enrollment, rotation/revocation controls, expiring metadata,
   consumer-event delivery, provider/tenant quota scheduling and circuit breakers.
4. Complete distributed cleanup/backup receipts, restore fences, KMS/scale-out adapters and
   independent security/operational acceptance.
5. Configure real operator identity, DNS/TLS, protected storage and provider credentials; then run
   authorized real-provider install-to-disconnect acceptance. Fake providers do not establish Google
   verification, API eligibility, real revocation or physical backup erasure.

See the service README, `releases/0.3.0.md` and `docs/remaining-work.md` for exact scope.
