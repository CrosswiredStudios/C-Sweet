# Operator recovery of failed agent onboarding

`AgentOnboardingRecoveryService` provides an explicit human recovery boundary for the
original `com.csweet.agent.onboarded.v1` delivery. It does not hire an agent, change
permissions, create a replacement event, or acknowledge onboarding on the agent's behalf.

## Review and recovery API

All routes beneath `/api/agents/installations/{installationId}/onboarding-recovery`
require `PluginAdministration`. The service additionally requires a currently active
human administrator who is a manager or owner of the specified business.

- `GET /?organizationId={businessId}` validates eligibility and returns the original
  event/work IDs, previous attempt count, permitted next attempt, and a ten-minute review token.
- `POST /` accepts `RecoverAgentOnboardingRequest` with that business ID and review token.
  It permits one further attempt of the same work item, with a server-owned one-hour deadline.
- `GET /receipt?organizationId={businessId}` reads the last persisted operator receipt.
  Use this after an uncertain response; do not generate replacement events or repeat unrelated actions.

Use the application's normal authenticated, CSRF-protected request flow. Never log review
tokens, protected payloads, request headers, provider credentials, or raw failure text.
The endpoints use `Cache-Control: no-store` and return fixed conflict diagnostics.
There is no new end-user recovery button in this change.

## Eligibility and preserved evidence

Recovery requires the current active, ready agent installation and built package; its
active employee and original conversation; the exact current-package delivery key and
source event; an unacknowledged Failed event; and DeadLetter work with no unfinished
attempt. Cancelled, completed, acknowledged, erased, empty, changed, or foreign deliveries
are rejected. The original protected payload must match both its hash and lifecycle event.

Connector bindings are validated against current business, resource, connection, package,
scope and consumer authority. The signed review fingerprint includes the reviewed
package, consuming grant, binding/authority snapshots, terminal work state and previous
recovery revision. Changed authority requires a fresh review. Managed-service execution
policy remains a separate gate and is not changed or independently probed by this API.

The original event/work IDs, idempotency and mutation keys, protected payload/result,
failure evidence and work attempts remain intact. A host-owned `PluginOperationalState`
under `OnboardingRecoveryBudget.Kind` stores the current immutable receipt and absolute
permitted attempt. Its existing revision token and unique key fence duplicate writes.
Each operator request also queues a structured audit event in the same transaction,
including the reviewed state hash, exact package identity/digest and consuming grant
revision. A later recovery may replace the current marker but cannot replace those
historical audit records.
An identical accepted request returns its saved receipt without queuing another attempt.

## Concurrency and attempt budget

`OnboardingDeliveryFence` uses the inbox's installation semaphore, then its PostgreSQL
`agent-work-claim` transaction advisory lock. Recovery, lifecycle dispatch and acknowledgement
use that order. Each API request owns a fresh database scope; pending incoming edits are
rejected before transaction cleanup can discard them. Existing inventory concurrency
fences remain intact. Claim/complete already hold these locks and must not acquire the semaphore
again. Dispatch reads current event and delivery state only after entering the fence.

The receipt limits the absolute attempt number, independent of resource-replacement or
maintenance retry counters. Marked deliveries do not enter the dispatcher's automatic
transient retry path. Claim checks the receipt's package, deadline, source event and
remaining budget before releasing the payload. The new deadline is normalized to PostgreSQL
microsecond precision, so database round trips preserve the receipt comparison.

A late acknowledgement cancels an unclaimed recovery instead of inventing a completed
attempt. Delivered and Cancelled events cannot be resurrected. Ordinary dispatcher failures
retain sanitized, bounded backoff, guarded against overwriting a newer recovery or acknowledgement.

Legacy connection disconnect preserves this host receipt while still purging provider data
and revoking connection authority. The receipt contains no provider content or credentials.
Permanent work-erasure tombstones continue to prohibit recovery; no erased payload is restored.

## Adoption and verification

The implementation uses existing tables and requires no new database columns or package
release. API and AgentHost must load the coordinated reviewed code before use. Publication,
deployment/restart and an actual operator recovery are separate actions.

`OperatorOnboardingRecoveryTests` covers authority, terminal state, review invalidation,
duplicate submission, acknowledgement ordering, shared-gate races, bounded retries,
redaction and dispatcher backoff. `OperatorOnboardingRecoveryRelationalTests` uses the real
SQLite-backed model with its foreign keys and transactions to verify independent contexts,
commit-failure rollback and stale cancellation/acknowledgement. Its timestamp/sequence
adapters are test-only accommodations for PostgreSQL storage conventions. Two additional
opt-in tests run on an explicitly configured disposable PostgreSQL fixture: an independent
database client holds the exact advisory lock and commits acknowledgement while recovery
waits, and a real timestamp round trip and inbox claim verify the one-attempt budget.
No production database, external provider, or isolated agent process is used by these tests.
