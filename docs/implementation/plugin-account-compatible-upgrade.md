# Connection-preserving account connector upgrades: investigation

Status: design only. No transfer endpoint, migration, credential adoption, grant transfer or
connection-preserving activation is implemented by this change. The staged update remains inactive.

## Existing boundary

`AgentInstallationService.UpdateAsync` builds a separate, disabled, zero-grant revision.
`ApproveUpdateAsync` requires the exact reviewed digest and blocks replacement while the old
revision has an account that is not `Disconnected`. This is an intentional lifecycle contract,
documented in `plugin-account-lifecycle.md` and tested by
`EnterpriseUpdateRequiresReviewedDigestAndConfirmedOldAccountCleanup`.

An installation lineage key is not an authorization alias. Core account groups, resources,
connections, secret references and business grants belong to the installation revision. Native
authority snapshots include the current digest and grant/account/resource/authorization revisions.
The service independently binds `ManagedConnection` to the deployment, installation revision,
package digest and provider-profile revision. `ManagedReadService.Read` and
`ManagedMediaService.Require` enforce those bindings. Removing the Core cleanup guard, changing
foreign keys, copying a managed context or substituting a digest would not establish valid
service authority. Approved package records and historical manifests must remain immutable.

## Smallest defensible extension

Provide an explicit, reviewed **compatible managed-account adoption** transaction, separate from
disconnect and data erasure. It needs coordinated service/Protocol and Core changes; a UI-only
patch cannot implement it safely. Customer-owned credentials should remain unsupported until a
separately tested host-owned transfer contract exists. There should be no provider-name switches.
Negotiate adoption support explicitly. An older service or unsupported contract must leave the
current connection intact rather than fall back to disconnect, field rewriting or another consent.

The service must independently verify both approved packages, active deployment/tenant and exact
old/new installation identities. Require a current provider-verified source in `Ready`, unchanged
provider/profile revision, no maintenance lease, and no uncertain or active media exchange. The
paired deployment signer must prove the exact source connection/revision, target digest/revision and
bounded, replay-protected adoption request. Equality of scope-set IDs alone is insufficient:
compare actual scope definitions, connection/provider destinations, identity/discovery/resource
contracts and lifecycle policy. Incompatible or unknown declarations fail closed.

The target's package approval and execution policy remain independent. Adoption cannot approve a
package, enable execution, add scopes or infer consent from a refreshed token. A compatible package
may still need a fresh business grant review. Any proposed preservation must show the exact same
business/resource/permission set, verify the definitions and mappings of the effective capabilities,
and bind explicit approval to the current old/new digests and revisions. Never infer ownership from
channel labels, add future-resource access, or restore agent bindings, pending plans or autonomy.
The service can verify deployment authority and compatibility, not independently prove a browser's
human click; Core must enforce that human review and the existing anti-forgery boundary.

Provider tokens stay inside the service vault. A service-owned serializable transaction would
create the target connection, retire the old execution authority without provider revocation,
and persist an immutable, content-free adoption receipt and outbox event. Preserve original
consent provenance and historical cleanup obligations. This receipt must not claim provider
revocation, physical deletion, backup erasure or a new Google consent.

Core needs a durable adoption journal and recoverable, idempotent finalization because its
database cannot atomically commit with the service database. Fence both stale plans and old
revision execution before activation; retain original provenance rather than silently rewriting
history. If the service commits but Core's acknowledgment or commit is lost, read/recover the
same receipt and complete forward. Do not start another consent, duplicate credentials, invent
a completed rollback or send old proof material to a changed service origin.

Tests must cover semantic permission changes under unchanged IDs, wrong tenant/deployment/
installation/digest/profile/identity, unapproved or revoked target packages, stale review,
concurrent disconnect/profile/package revocation, refresh leases, active media, repeated requests,
lost replies and crashes at each commit boundary. Assert zero provider revocations and no new
OAuth scopes, grants, agent bindings or completed deletion receipts. Real adoption and deployment
remain separate actions after source review and synthetic integration verification.

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
does not implement the compatible adoption design above or any physical erasure service.
