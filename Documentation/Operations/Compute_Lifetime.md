# Compute lifetime

The local application-testing policy defaults to retaining compute until explicitly released. A specification with `lifetimeSeconds: 0` requires a scoped grant whose `maximumLifetimeSeconds` is zero and whose expiry is `DateTimeOffset.MaxValue`. Positive lifetimes remain timed leases. A finite grant cannot authorize an until-release request.

`CSweet:Compute:Defaults:MaximumLifetimeSeconds` configures the default grant ceiling (zero means until released). Software Developer 1.6.0 exposes `computeLifetimeSeconds`, default zero. Its request still has to fit platform grants. Resource and concurrency limits remain enforced; network grants remain explicit and specific to one instance.

The wire/database representation for no scheduled expiry is `DateTimeOffset.MaxValue`; the Compute page displays **Until released**. The provider retains this signed reservation across restarts and does not reclaim it merely because time passes. SDK 3.46.1 accepts the zero-lifetime request. The existing typed `DestroyAsync` API releases compute. Business owners can also use **Release instance** on Compute, with confirmation; destruction discards VM-local files and leaves the saved C-Sweet repository intact.

`OrganizationDataPurgeService.PurgeAsync` requests compute destruction when business deletion
encounters instances without `TeardownConfirmedAt`. Each attempt handles at most 100 instances
through `IComputeBroker.ChangeLifecycleAsync`, retaining current capability/grant checks and
atomic operation, audit and provider-wake persistence. Pending destruction is reused; failed or
blocked destruction can be retried with a new generation. Agent workloads and business records
are retained until provider evidence confirms VM and private-disk teardown for every instance.

`CoreOrganizationService.DeleteAsync` returns `compute_cleanup_pending` while that cleanup is
outstanding and `compute_cleanup_failed` if cleanup cannot be requested. `Organizations` shows
pending cleanup as progress and links to that business's Compute page. Retry **Delete business**
after cleanup finishes to complete deletion; the first request does not schedule an automatic
business purge. If current agent authority is unavailable, cleanup remains blocked and needs
operator attention; deleting database records or treating stopped VMs as destroyed is not a repair.
`OrganizationDeletionTests` and `EnterpriseWorkspaceTests` cover this lifecycle and UI recovery.

On access discovery, Core upgrades only the recognized legacy automatic one-hour policy with unchanged resource/network/template constraints and revision 1 or 2. Revoked, expired, customized, and network grants are preserved. Grant changes, audit records, and recovery wake events commit together. No expired or destroyed VM is resurrected.

Rollout requires the updated Core, Compute provider, SDK and agent. Existing provisioned leases are immutable signed terms stored independently in Core and the provider journal: they retain their original expiry. A replacement uses the new policy after rollout. Do not extend only the database timestamp or alter the protected provider journal. Replacement instances need their own explicit network grant.

Software Developer retains the full Docker build log in the VM and returns a bounded error tail to the repair model. `maximumDeploymentRepairs`, `maximumPlanRepairs`, and `deploymentDiagnosticCharacters` configure its repair behavior. A build task finishing does not release the delivered testing instance.
