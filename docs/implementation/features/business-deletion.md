# Business deletion

`CoreOrganizationService.DeleteAsync` persists `OrganizationStatus.Deleting` before
requesting compute cleanup through `OrganizationDataPurgeService`. The existing
`compute_cleanup_pending` response means deletion is continuing; it does not require
another owner request. No schema migration is needed: organization status is already
stored as a string.

`OrganizationDeletionWorker` runs bounded discovery through `OrganizationDeletionRecovery`
every three seconds, including after API restart. `CoreOrganizationService.ContinueDeletionAsync`
rechecks current intent under `OrganizationDeletionLock`. Owner requests and recovery
share a process gate and PostgreSQL session advisory lock, allowing the purge to retain
its existing transaction boundary. Compute lifecycle intent, audit and provider wakes
continue to commit together through the compute broker. This adds no agent polling loop.

The purge retains business data until **all** compute environments have confirmed VM
and disk teardown. Discovery advances batches of cleanup requests without duplicating
queued destroy operations. Current failed or blocked destroy operations stop recovery
with `DeletionFailed`; only an explicit owner retry requests another generation.
Other structured purge failures also retain a visible failure state.
An active destroy operation still unconfirmed ten minutes after `CreatedAt`
and no live dispatch lease also surfaces `DeletionFailed`. This retains all business
data and compute reservations; elapsed time never counts as teardown evidence.
An explicit owner retry supersedes that stalled operation through the compute broker
and requests a new generation. Fresh pending operations and live dispatch leases are
left alone. Repeated observations of `Destroying` do not reset this deadline.
`CoreOrganizationService` also records unexpected non-cancellation purge
exceptions as deletion failures so they cannot leave an indefinitely busy tile.
`ComputeBroker.RequireActorAsync` blocks new compute activity for deleting or failed
deletions while allowing reads, stops and destroys. PostgreSQL admissions hold a shared
organization-row lock until commit, so saving deletion intent waits for earlier
admissions and later ones cannot race cleanup.

`AgentInstallationService.QuiesceAsync` loads only schedules and runtimes and retries
schedule concurrency conflicts against fresh state up to three times. Each retry
discards the failed tracked mutation and its unsaved outboxes before reapplying the
disable/session-revocation operation. Persisting scheduler conflicts are returned as
`agent_cleanup_pending`; recovery advances them automatically without purging data
before workload cleanup succeeds.

`Organizations.razor` keeps the tile busy, disables its normal actions, and shows
“Cleaning up resources…” while deletion is pending. Its two-second status reads run only
while a pending tile is present, stop on disposal, and never issue DELETE requests.
Reopening Enterprise restores persisted progress or the recovery action. Completion
removes the tile and calls `BusinessContext.RemoveDeletedBusinessAsync` to update focus
and local storage. Refresh failures retain progress and explain that server cleanup
continues. The confirmation dialog explains automatic completion.
Pending tiles keep **View compute cleanup** and **Retry deletion** available. Retry
still requires confirmation and never duplicates a fresh in-flight destroy request.

If the Windows provider has stopped discovering work, restart only the affected
business's `CSweet.Compute.HyperV.<business-id-without-hyphens>` service from an
administrator PowerShell, then inspect Compute and retry deletion if needed. Provider
recovery must confirm VM and disk removal before the data purge can finish.

`HyperVComputeDriver.ProtectRemovedWorkloadScript` also handles Hyper-V crash dumps
owned by the removed VM worker account. It derives only that VM's exact SID from
the recorded VM GUID, first verifies VM absence and rejects linked entries or other
owners, then restores Administrators/SYSTEM ownership and access before normal
workload removal. A retained `Memory.dmp` under the VM's metadata directory can
otherwise stop intake after VM removal and leave the private VHDX behind. This
exception applies only to teardown; runtime, journal and payload ownership rules
remain enforced by `WindowsComputeProtectedPaths` and `HyperVWorkloadProtection`.

Regression coverage lives in `OrganizationDeletionTests`, `EnterpriseWorkspaceTests`,
`BusinessDeletionUiStateTests`, `AgentInstallationServiceTests` and `ComputeBrokerTests`,
including signed teardown, duplicate discovery, schedule conflicts, admission fencing,
failed-cleanup recovery, tile progress, reload and focused-business removal.
