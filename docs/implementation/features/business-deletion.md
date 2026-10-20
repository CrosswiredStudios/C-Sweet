# Business deletion

`CoreOrganizationService.DeleteAsync` persists `OrganizationStatus.Deleting` before
requesting compute cleanup through `OrganizationDataPurgeService`. The existing
`compute_cleanup_pending` response means deletion is continuing; it does not require
another owner request. `DurableResourceCleanup` adds the host-owned deletion outbox;
apply this migration before running the updated API and gateway.

## Employee runtime removal and storage deletion

`OrganizationUserService.DeleteAsync` archives the employee and enqueues an
`installation-runtime` `ResourceCleanupRequest` in the same transaction that disables
the installation. Firing retains employee/audit history and the shared software catalog;
it destroys the employee's execution VM and private disks. `ResourceCleanupWorker` runs
bounded discovery after startup and every three seconds. `ResourceCleanupProcessor`
uses a conditional database lease, bounded execution, capped error backoff and idempotent
effects. Requests have no cascading foreign keys and retain their completion receipts.
An offline Office keeps its request pending; neither elapsed time nor cancellation is
physical teardown evidence. Rehire/enable and runtime assignment admission reject pending
runtime deletion, and PostgreSQL activation locks serialize enabling with firing.

`InstallationRuntimeCleanup` first persists `Stopping` and revokes broker sessions, then
discovers **all** fleet assignments for each runtime, including startup interrupted before
a provider handle was saved. `FleetAgentWorkloadRunner.InspectAsync` requires versioned
stop evidence and confirmation for every retained attempt, including fenced attempts on
an old Office. Only confirmed teardown clears runtime locators. Business quiescence also
cancels business-scoped toolchain/build assignments; shared catalog builds are retained.
`AgentRuntimeCleanupService` cannot age away unconfirmed runtime/assignment evidence.

After Compute and Office teardown, `OrganizationDataPurgeService` enqueues
`business-storage` and returns `storage_cleanup_pending`. It retains the organization and
storage rows until the worker has confirmed deletion. Failed filesystem/NAS/S3 operations
retry automatically; a failed or cancelled advance never drops the locators needed to retry.
`BusinessStorageCleanup` removes media bytes and unfinished uploads, business workspace
snapshots, attached repository-onboarding staging and GitHost-owned business storage.
Media locators referenced by another business are preserved. `BusinessStorageAdmission`
drains admitted media/snapshot/hiring writers before committing deletion intent and prevents
new writes while deletion is pending, including on another API instance.

`InternalGitRepositoryStore.DeleteBusinessStorageAsync` uses a cross-process
`BusinessStorageLease` shared with repository operations and backup workers. Its durable
tombstone rejects late writes. It removes only the exact business's internal repositories,
GitHub caches, backup jobs/schedules, LFS and backup prefixes. Custom mounts must pass
their configured store-identity checks. S3 deletion includes historical versions and delete
markers and never lists/deletes outside the exact prefix ending in `<business-id>/`.
Read-only backup inspection continues to work when live repository storage is unavailable.
`OwnedStorage` enforces resolved descendant paths and refuses links in ancestors or the tree.
New Git/LFS/import/backup temporaries use `TemporaryRoot/businesses/<business-id>`;
workspace import temporaries live under the business's snapshot directory. Interrupted
writes can therefore be reclaimed by the same ownership-scoped cleanup. Historical
unscoped temporary files without ownership evidence are never swept by business deletion.

`ComputeProviderRetirementService` retains a separate `local-compute-storage` receipt for
installed provider storage. New privileged installs register
`Register-ComputeBusinessRetirement.ps1` before preparing images or copying payloads, so an
interrupted install remains recoverable even without enrollment. The protected SYSTEM task
rechecks current Core deletion authority over pinned HTTPS before deleting anything.
It waits for installer completion and the business storage phase. The first installer
launch being denied is explicitly distinguished from an uncertain interrupted launch.
Updated providers also recover the request through existing signed work discovery and
`ComputeLocalRetirement.ScheduleAsync`; discovery itself is not teardown evidence.

`Retire-ComputeBusiness.ps1` checks the exact business/service/enrollment, all VM/checkpoint
media and VHD parent chains, refuses shared image references and linked paths, stops/removes
the business service and deletes its protected provider root. It removes only dedicated
installer signing keys/certificates, including partial configuration remnants. A legacy
shared provider, Office installation, other businesses, shared catalog packages and shared
base-image build caches are retained. A one-purpose encrypted receipt capability permits
only retirement authorization/acknowledgement; no caller-selected deletion paths or general
execution grants are accepted. A lost callback retries from the persistent machine task.
Core then removes this setup's user staging. Durable receipts/tombstones are platform
recovery metadata, not retained business payloads.
`Reset-CSweetLocal.ps1` inventories `ComputeRetirement` and validates the exact scheduled
task name/action before stopping and unregistering retirement tasks during an explicit reset.

Existing providers must receive the updated provider runtime (or be prepared again through
the normal setup flow) to handle retirement discovery. An old/offline provider or an
unconfirmed historical partial installation leaves deletion pending instead of reporting
success. These source changes do not patch installed runtime payloads or perform a live reset.

Regression coverage: `ResourceCleanupTests`, `EmployeeActionExpirationTests`,
`AgentInstallationServiceTests`, `AgentRuntimeCleanupServiceTests`,
`EnterpriseWorkspaceTests`, `WorkspaceVolumeBridgeTests` and
`scripts/tests/ComputeBusinessRetirement.Tests.ps1`. The machine tests mock Hyper-V,
services, HTTP callbacks and Task Scheduler and use temporary filesystem fixtures; a real
privileged machine teardown still requires release acceptance on a disposable installation.

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
