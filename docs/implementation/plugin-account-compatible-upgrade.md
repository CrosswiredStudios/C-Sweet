# Compatible managed-account upgrades

`PluginAccountUpgradeService` implements an explicit alternative to standard account deletion and
connector replacement. It is generic and uses native enterprise accounts/resources; no provider
name or channel label selects authority. Standard `ApproveUpdateAsync` retains its existing cleanup
guard. Compatible adoption is a separately reviewed operation in the staged plugin update panel.

## Supported review

The initial implementation accepts one connected provider-verified managed account and authorization,
reviewed by its original consenting administrator. The source must be an active enabled system
connector and the target its built, disabled, zero-grant staged revision from the same repository
and installation lineage. Customer-owned credentials, multiple authorizations, active consumer
bindings/operators, pending consent/revocation/deletion, leases and unresolved execution/media work
are rejected. No agent bindings, schedules, execution policies or autonomy are transferred.

Both manifests must pass SDK validation. Protocol `CompatibleConnectionContract.Require` requires
identical root declarations except version/provider operations, and identical operations executable
under already granted scope sets. Actual scope definitions, identity/resource mappings, schemas,
destinations, lifecycle policy and unknown fields must match. Changing an ungranted operation does
not grant its scope. Existing selected-resource business grants retain the same business, resource
IDs and capabilities; their package digest and revisions advance only with successful adoption.
Whole-account/future-resource grants are unsupported. Tenant restrictions of the paired deployment
are unchanged; an installation's enterprise scope is not a remote tenant grant.

## Durable boundaries and recovery

Core authenticates the original administrator and requires anti-forgery protection for each unsafe
API request. A bounded protected review token expires after ten minutes and binds the actor, exact
packages, original consent session, current native inventory and service review. Review performs
no adoption. Approval first saves a Pending journal and disables the source in one serializable
transaction. A lost response never silently re-enables it or creates new consent.

The service independently checks active package/profile approval, exact deployment/installation/
connection/consent-session binding, unchanged scopes and current Ready provider-verified inventory
within the original lifecycle authorization-check window. It changes the same connection's current
package/installation binding, advances its revision and writes an immutable receipt atomically.
Provider credential/inventory bytes and original consent remain unchanged; no token leaves the
service, no provider revocation occurs and no execution policy is created.

Core reconciles the same receipt before repeating the same idempotent intent. A fresh receipt,
paired origin/profile and native snapshot are checked again inside local finalization. The same
native account/resource/connection IDs, selected grants and encrypted managed-context reference
move atomically to the target; the source is retired and target activated. Historical consent and
cleanup records stay attached to their original provenance. `ManagedAccountProvenance.Original`
requires that original session and a completed adoption journal for subsequent maintenance or
actual disconnect. It does not manufacture proof of erasure.

Explicit cancellation records a service tombstone if adoption has not committed, preventing a late
request from adopting. An already accepted adoption completes forward; it is never reported as a
rollback. Source access resumes only if its exact authority remains unchanged. Otherwise it stays
fenced with CancelledReviewRequired. Status reads do not automatically submit or resume. UI and
client timeouts include response-body reads, display content-free uncertainty and require an
explicit status/reconciliation action. No automatic second POST is scheduled.

## Adoption prerequisites and verification limits

1. Review, separately publish Protocol 0.7.0 and deploy the reviewed service schema/code first.
2. Back up Core, apply `AddCompatiblePluginAccountUpgrades`, then deploy API/UI/AgentHost together.
   The additive journal has concurrency/unique-pending guards, immutable authority/terminal rows
   and an evidence-preserving rollback that refuses to discard existing intents.
3. Independently approve the exact target package/profile on the service. This is not done by Core.
4. Review the staged package's compatible path and explicitly approve that exact native/service
   snapshot. Live adoption and any later execution policy remain separate operator decisions.

An older/missing endpoint fails closed without falling back to disconnect or field rewriting.
The database boundary is recoverable, not distributed atomicity: accepted service changes may
leave a locally paused Pending operation for explicit reconciliation. Historical backup obligations
and missing cleanup handlers remain visible. Do not downgrade databases containing adoption
receipts or delete the journal as a rollback technique.

Synthetic tests exercise service signatures/transactions, compatibility, cancellation, exact grants,
lost acknowledgments, local commit rollback, native maintenance/disconnect provenance, API auth/
anti-forgery, bounded client failures and component state transitions. Model parity and migration SQL are checked offline in the regular suite. Opt-in
`PluginAccountUpgradePostgresTests` apply the actual additive migration to a fresh full EF model
in a disposable PostgreSQL instance, exercise insert/update/delete/truncate/rollback guards and
unique pending fences, and use observed row-lock barriers for competing approvals, normal Resume
and concurrent business revocation. The opt-in fixture requires an explicit settings file named by
`CSWEET_ADOPTION_POSTGRES_TEST_FILE`, pointing to its dedicated isolated test server, and creates
randomly named synthetic databases. It must never reuse application credentials or databases.
These tests do not validate the complete historical migration chain or a production backup restore.
SQLite tests remain in the regular suite; their adapters do not establish PostgreSQL behavior.
Browser QA with the real update component and a separate memory-only fake API checks explicit
approval, completion navigation, lost-acknowledgment recovery, cancellation, in-flight controls,
redaction and narrow-screen identity visibility. It does not exercise live browser authentication,
a real service or provider consent. No real provider acceptance, live adoption, production migration,
package publication or physical erasure is established by these source tests.

## Cleanup is an independent deployment obligation

`PluginSignedCleanupParticipant` is registered for `external:backups` and `external:processors`.
It reports unconfigured unless deployment-owned destination/receipt-key trust and the mTLS
transport are ready. Registration alone is not erasure. These are not paid-service requirements;
the operator must supply an implementation tied to the actual backup or processor stores.

`DisconnectAsync` immediately revokes local authority, enqueues provider revocation, records a
backup obligation and starts content erasure. `ProcessDeletions` cannot complete until every
recorded external obligation and provider revocation has independent confirmation. Managed
connections also record `external:managed-service-backups`; no built-in participant for that
store exists. Configuring the first two participants alone therefore does not establish complete
managed-account cleanup. Do not remove an obligation or declare that no copies exist merely
because no endpoint is configured.

The implemented diagnostics change discovers outstanding external stores from the installation's
recorded account inventory as well as deployment defaults. It reveals missing coverage, including
managed-service backups, without issuing cleanup requests or changing readiness/authority. It
does not implement a physical erasure service. Compatible adoption retains these obligations.
