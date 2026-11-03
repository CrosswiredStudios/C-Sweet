# Managed provider execution and media relay

`ManagedConnectorRelay` joins existing Core authority to the provider-neutral service
execution boundary. `ConnectorReadExecutor` uses it for managed reads; ownership mappings
execute independently at the service. `ConnectorMediaTransport` uses it before the direct
token/provider path. Other, unsupported mutation mappings remain fail closed.

`FrozenConnectorPlan.OriginalInput` is additive and omitted when null, preserving legacy
plan serialization. New plans bind original input into the canonical plan hash. Old plans
without this input cannot use the managed relay and must be prepared/approved again.

The relay requires an exact persisted Executing plan, current native account/resource
authority and business/consumer grants, matching input/plan hashes, and (for media) the
existing `ConnectorActionApprovalService.RequireApprovedAsync` decision. It rechecks those
before and after remote execution. Enterprise connector BusinessId is not treated as the
customer tenant. The tenant comes from the validated business; the resource comes from the
native account authority, including connectors with no legacy BoundResourceId.

The service receives signed plan and approval attestations. It does not independently
prove human approval or attachment access. Core retains the existing conversation/message
attachment binding, asset SHA/size/type, ingestion proofs and per-range integrity checks
in `ConnectorMediaSourceService`, `MediaAssetIntegrity` and `ConnectorMediaTransferService`.

`ManagedPluginConnectionGateway` uses the existing protected deployment registration and
exact origin provenance. A stored managed context prevents any local-token fallback after
a mode switch. No new key, registration, owner authority or provider credential is created.

For media, Core stores only a `ManagedMediaHandle` in its protected response-session key.
Provider URLs remain encrypted at the service. The existing durable worker still records
InFlight before each exchange, probes after interrupted chunks and refuses automatic
replacement initiation. It accepts only a strictly parsed handle and binds it to the exact
remote connection/plan/media receipt. Failed managed transfers retain a durable cancellation
cleanup obligation. Local cancellation does not assert a provider deleted or aborted content.

The protected v2 handle retains the exact canonical service origin, connector/connection
IDs, transfer ID and binding hash independently of `managed.context`. `CancelAsync` validates
these correlations and uses the original paired deployment's cleanup path after account
revocation/deletion has erased its context and frozen plan. It cannot authorize new execution,
redirect cleanup to a newly configured service, or certify retained-backup deletion. Unsupported
legacy handles fail closed rather than inventing missing provenance; this relay is unreleased.

Pairing is tenant-specific. A system-wide connector installation (`BusinessId = enterprise`)
does not give its paired deployment access to every business. The service independently
requires the requested business tenant to match that deployment and its explicit policy;
another business remains rejected and needs its own authorized deployment. Never relax
that check to accommodate sharing a connector installation across businesses.

## Adoption prerequisites

- Review the service read/media changes and explicit additive service schema upgrade first.
- Publish the final Protocol 0.6.0 package separately, then restore/recheck Core from nuget.org.
  Both service read and media changes must be included before the first 0.6.0 publication;
  the interim read-stage local package does not contain the required media contracts.
  Local tests use an isolated candidate feed/cache; public CI cannot restore an unpublished
  dependency. Do not copy a candidate into shared/global caches or call local restore public CI.
- The current YouTube connector 0.2.1 upload mapping lacks a response resource pointer required
  by the relay. A new reviewed package/version and service package approval are needed.
- Service execution policies default to deny. Exact deployment/tenant/package read/media
  policy configuration, upload OAuth scope, business/agent grants and real posting remain
  separate explicit access decisions. Existing pairing/base consent is not an upload grant.
- The separately reviewed dependent-agent manifest fix is also needed for the YouTube agent.
  Agent installation, hiring and activation are separate; recurring monitoring is not enabled
  by this relay change.

Synthetic tests validate Core decisions/native grants, asset provenance and broker behavior,
plus service-side signed HTTP/durable recovery and packaged Protocol consumers. They do not
prove an actual video upload, provider processing, final channel visibility or backup erasure.

The account-import workflow retains both the focused setup/settings UI regressions and
the connector/managed-relay authority and cleanup tests. The plugin-lifecycle workflow
also checks catalog acceptance of validated dependency-consuming protocol 2.4 agents.
