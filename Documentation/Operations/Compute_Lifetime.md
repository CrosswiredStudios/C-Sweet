# Compute lifetime

The local application-testing policy defaults to retaining compute until explicitly released. A specification with `lifetimeSeconds: 0` requires a scoped grant whose `maximumLifetimeSeconds` is zero and whose expiry is `DateTimeOffset.MaxValue`. Positive lifetimes remain timed leases. A finite grant cannot authorize an until-release request.

`CSweet:Compute:Defaults:MaximumLifetimeSeconds` configures the default grant ceiling (zero means until released). Software Developer 1.6.0 exposes `computeLifetimeSeconds`, default zero. Its request still has to fit platform grants. Resource and concurrency limits remain enforced; network grants remain explicit and specific to one instance.

The wire/database representation for no scheduled expiry is `DateTimeOffset.MaxValue`; the Compute page displays **Until released**. The provider retains this signed reservation across restarts and does not reclaim it merely because time passes. SDK 3.46.0 accepts the zero-lifetime request. The existing typed `DestroyAsync` API releases compute. Business owners can also use **Release instance** on Compute, with confirmation; destruction discards VM-local files and leaves the saved C-Sweet repository intact.

On access discovery, Core upgrades only the recognized legacy automatic one-hour policy with unchanged resource/network/template constraints and revision 1 or 2. Revoked, expired, customized, and network grants are preserved. Grant changes, audit records, and recovery wake events commit together. No expired or destroyed VM is resurrected.

Rollout requires the updated Core, Compute provider, SDK and agent. Existing provisioned leases are immutable signed terms stored independently in Core and the provider journal: they retain their original expiry. A replacement uses the new policy after rollout. Do not extend only the database timestamp or alter the protected provider journal. Replacement instances need their own explicit network grant.

Software Developer retains the full Docker build log in the VM and returns a bounded error tail to the repair model. `maximumDeploymentRepairs`, `maximumPlanRepairs`, and `deploymentDiagnosticCharacters` configure its repair behavior. A build task finishing does not release the delivered testing instance.
