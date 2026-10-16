# Acceptance scenarios and evidence

[Overview](README.md) · [Decisions](09-decisions-and-questions.md) · [Sources](11-evidence-and-sources.md)

**Status: Proposed acceptance criteria.** None of the new hosted-platform or model
connector scenarios below is marked passed by this documentation work.

## Evidence levels

| Level | Establishes | Does not establish |
|---|---|---|
| Source/document inspection | Existing design and code paths | Runtime correctness |
| Deterministic unit/protocol tests | Particular state transitions and failure handling | Real device/network/provider compatibility |
| Installed local integration | Packaged components working on a specified machine | Public NAT/proxy behavior or cloud certification |
| Public-network integration | Actual external client-to-gateway path | Capacity or service availability under load |
| Provider/hardware certification | Tested image/provider/workload combination | All operating systems, images, or future upgrades |
| Pilot operating evidence | Actual costs, incidents, support, and recovery | An unbounded performance or compliance guarantee |

Retain deployment version, relevant package versions, provider/model version,
resource settings, sanitized correlation IDs, measured results, and failure
evidence. Do not put credentials or raw customer content into test reports.

## Product configuration coverage

| ID | Execution | Inference | Core proof |
|---|---|---|---|
| A-01 | Customer Office | BYOK | Hosted HQ completes bounded work without receiving a hosted-compute charge |
| A-02 | Customer Office | Private connector on same computer | Local endpoint works without public exposure |
| A-03 | Customer Office | Private connector on another computer | Connector works independently of Office/Hyper-V |
| A-04 | Hosted Office | Customer private connector | Model location is independent of execution location |
| A-05 | Hosted Office | Managed model | Budget reservation, usage settlement, and Office metering reconcile |
| A-06 | Mixed Offices | Per-agent model mix | Placement and model policy both hold; no accidental paid fallback |
| A-07 | Self-hosted HQ | Existing supported provider | New commercial services are not mandatory for self-hosting |

Use the same small task where possible: create a bounded plan/document, perform a
small source change with review, and exercise one background inference operation.
Document tools/capabilities needed rather than substituting an unrelated hello-world
call for the agent workflow.

## Worked test: first BYO customer

**Given:** a fresh test account, isolated Headquarters deployment, signed Office
package, compatible test computer, supported provider key, and authorized staffing budget.

**When:** the owner provisions Headquarters, links Office with a one-use code,
approves its fingerprint/resources, validates the model profile, hires three
employees, and starts one bounded assignment.

**Then:** owner bootstrap cannot be claimed by another visitor; Office is both
connected and certified before dispatch; assignment and results belong to that
customer; the model key stays out of the guest; usage has immutable attribution;
the invoice preview contains one own-Office subscription, the correct distinct
active agents, no hosted Office-hours, and no C-Sweet BYOK token fee.

**Repeat:** duplicate the enrollment submission, reconnect the Office, replay the
completion event, and upgrade an agent package. These actions must not create a
second Office subscription, duplicate work, or a new employee billing identity.

## Connectivity and authorization cases

| ID | Scenario | Required result |
|---|---|---|
| N-01 | Office behind home NAT, no inbound forwarding, no Tailscale | Actual public-gateway enrollment, stream, workload, and reconnect succeed |
| N-02 | Model-only computer under the same conditions | Approved inference operation streams successfully |
| N-03 | Network blocks required HTTP/2/client authentication | Actionable failure; no weakened TLS or claimed readiness |
| N-04 | Office/connector credentials revoked during a live session | New authority denied and in-flight handling follows revocation policy |
| N-05 | Certificate expires during long downtime | Supported identity-proof recovery succeeds or clearly requires relinking |
| N-06 | Gateway leaf certificate renews | Tested pin/renewal procedure preserves valid trust without bypass |
| N-07 | Customer A sends Customer B's Office/connector/profile IDs | Rejected; no metadata or content leak |
| N-08 | Private endpoint redirects or DNS resolves unexpectedly | Connector refuses destination broadening |
| N-09 | Agent attempts host/LAN/private-metadata access | Denied except the exact model operation through its approved broker route |
| N-10 | Gateway process restarts or another instance owns the session | Reconcile authority and affinity; no duplicate assignment or orphaned stream |

## Recovery and resource cases

| ID | Scenario | Required result |
|---|---|---|
| R-01 | PC sleeps before dispatch | Work waits with a clear reason |
| R-02 | Office disappears during tool execution | Preserve uncertain attempt; fence/reconcile before replacement |
| R-03 | Connector disconnects after request dispatch | Unknown outcome visible; no automatic duplicate generation |
| R-04 | User cancels a long streamed answer | Cancellation propagates; resources released when confirmed |
| R-05 | Slow consumer and large response | Buffers/frames bounded; no unbounded gateway memory growth |
| R-06 | Two workloads fit nominal slots but exceed RAM | Queue or deny placement; do not overcommit safety limits |
| R-07 | Provisioning stops between external resource creation and local commit | Reconcile the resource; no duplicate tenant/subscription |
| R-08 | Restore customer backup to clean deployment | Data, repository refs, keys, roles, and endpoint identities verified together |
| R-09 | Paid model or hosted fallback is available but unauthorized | Continue waiting; no new charge |
| R-10 | Model lacks required tool/structured-output capability | Fail readiness or route only under approved capability-aware policy |

## Billing cases

| ID | Scenario | Required result |
|---|---|---|
| B-01 | Same employee retries or upgrades during month | One active-agent charge under the chosen activation policy |
| B-02 | A hired employee never works | No active-agent charge |
| B-03 | Background work activates a dormant employee | Chosen Q-03 policy and owner staffing budget enforced |
| B-04 | Two simultaneous 30-minute workloads on one hosted Office | 0.5 Office-hours, not 1; resource limits still enforced |
| B-05 | Local Office executes for 100 hours | No hosted compute-hour charge |
| B-06 | Two inference calls race for the last available balance | Atomic reservation prevents double spending |
| B-07 | Provider streams repeated cumulative usage updates | Single settlement; token subsets not double-counted |
| B-08 | Provider call ends without final usage | Unresolved coverage; no invented bill or false zero |
| B-09 | Customer model reports enormous or negative usage | Validate telemetry; cannot change trusted managed charges/credits |
| B-10 | Payment webhooks duplicate or arrive out of order | Idempotent entitlement and invoice handling |
| B-11 | Endpoint/model changes between reservation and dispatch | Revalidate revision/rate; do not apply stale authorization |
| B-12 | Subscription cancelled, payment fails, or device unlinked | Chosen grace/proration/drain/export policy applied consistently |

## Migration drill

Start with a completed task, one waiting task, repositories/artifacts, a protected
provider reference, and recorded billing activity. Drain/fence source, export,
restore onto a new host, validate digests, switch routing, reconnect devices, then
complete the waiting task. Verify no two writers, no lost history, no duplicate
charges, and unchanged customer authorization.

Repeat with a failure before cutover and a failure after the target accepts writes.
The second case must prove reconciliation rather than pretending a DNS rollback
restores new data to the old server.

## Readiness gates

| Gate | Minimum evidence | May claim |
|---|---|---|
| G0 — reviewed feature | Decisions and examples reconciled | Agreed product intent |
| G1 — private installed validation | V0–V5 from owned-server guide | Functionality on named test hardware |
| G2 — customer boundary validation | V6 plus N-07/N-08/N-09 | Tested negative authorization boundaries |
| G3 — public connectivity | V7, N-01/N-02/N-05/N-06 | Tested no-inbound-port customer path |
| G4 — commercial correctness | Q-01/Q-03 resolved; billing cases and reconciliation | Reviewed and tested charge semantics |
| G5 — paid pilot operations | Restore/migration drill, costs, capacity, support policies | Bounded paid pilot offering |
| G6 — cloud offering | Target-provider certification, capacity, recovery, security review | Claims restricted to verified cloud configuration |

## Documentation acceptance for this change

The documentation itself should have resolvable local links, consistent decision
labels, syntactically valid Mermaid diagrams, reproducible cost arithmetic, dated
external references, and a clear distinction between inspected behavior and new
design. This check does not execute or certify the proposed product scenarios.
