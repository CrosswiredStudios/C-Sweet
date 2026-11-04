# Retained setup history during compatible account upgrades

`PluginAccountUpgradeService.Require` still selects exactly one usable, connected,
provider-verified managed authorization. The administrator who originally consented
must review it. Package, profile, origin, scope, resource and business-grant checks
are unchanged.

An abandoned setup can leave a second administrative account group in
`ConnectionRequired`, even though its connection is already `Revoked` and its setup
has expired. Counting that group as a second usable account prevented a compatible
upgrade. This recovery path leaves those records on their original installation;
it neither deletes them nor marks them disconnected or erased.

`HistoricalAccounts` excludes such a group from the usable-account count only when:

- It is administrative, unverified and still `ConnectionRequired`, belongs to the
  same source installation and provider, and has no identity authority.
- Every linked connection belongs to that installation and group, is `Revoked`,
  and has no subject, bound resource, credential attempt or refresh lease.
- Every linked setup belongs to the same installation and a linked connection,
  has expired, and is `Expired`, `Cancelled` or `ImportFailed`. Maintenance,
  identity/discovery results, retirement keys and leases are rejected. Both
  connection and setup inventories must be nonempty and mutually accounted for.
- There are no native resources, resource-authorization proofs or business grants,
  including unavailable resources and revoked historical grants.

The existing exact secret-inventory check permits only the current authorization's
protected reference. The existing checks for unresolved operations, deletions,
revocations, operators, bindings and schedules remain in force.

`Revoked` is a local execution fence, **not proof of provider erasure or completed
backup cleanup**. A historical `RevokedAt` may be absent; this change does not fill
it in, restore authority or assert that erasure occurred. Recorded copies, cleanup
obligations, setup history and revoked connections stay unchanged on the source.
Only the selected verified account's existing records and permissions can move.

The review hash includes excluded historical group/connection metadata and bounded
setup eligibility metadata. `ApproveAsync`, `ResumeAsync` and cancellation recovery
recompute it, so changed history invalidates a stale review or keeps a pending
intent fenced. The prior snapshot hash is preserved when no history is excluded,
so this change does not invalidate existing ordinary pending intents merely by
introducing an empty-history field.

`PluginAccountUpgradeHistoricalGroupTests` covers exact history/copy preservation,
unsafe lookalikes, missing links, cross-installation relationships, secret and proof
retention, stale reviews, lost acknowledgments, transactional rollback and receipt
recovery. Opt-in PostgreSQL tests exercise the actual migration guards and an
independent-context historical change.

No schema, Protocol or provider-scope change is needed. Deployment and a fresh
exact compatible-upgrade review/approval are separate from preparing this source
fix. Do not repair live eligibility by deleting records or disabling these checks.
