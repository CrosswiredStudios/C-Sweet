# Manager project health and escalation

`ProjectHealthService` owns persisted review state, incident episodes, escalation deadlines, and human
delivery. `ProjectHealthReader` projects project setup, tickets (including project-scoped personal
commitments), current execution stages, coordination, chat failures, repository setup, live leases,
approvals, scheduled sprints, and runtime failures. These are generic platform records; game-specific
agent workflows do not run inside the platform.

## Detection

`CSweetDbContext.CaptureProjectHealthSignals` saves sanitized source signals in the source transaction.
Successful artifact, planning, coordination, and completion changes count as progress; health work,
lease renewal, and repeated errors do not. Exact diagnostic-ID runtime blocks are retained when
available, including when the runtime excerpt precedes the attempt failure receipt.

`ProjectHealthWorker` processes bounded discovery batches every 15 seconds. Assigned projects are
reconciled at least every minute under normal load. Explicit failures do not wait for the idle
threshold. Otherwise an active project with no real execution, recorded wait, or meaningful progress
for 15 minutes produces an incident. Live leases honor execution deadlines. A Running ticket alone
does not establish active work. New and existing assigned projects are discovered automatically.

`ProjectHealthState.LastProgressAt` survives restart. Recorded approvals, pauses and future work are
distinguished from idle projects. A timed wait is reconsidered after its deadline. Project assessment
failures are isolated so other projects and escalation deadlines continue processing.

## Incidents and delivery

`ProjectIncident` stores the initial evidence before a targeted Producer wake. Unique open episode
fingerprints coalesce continuous idle periods and repeated failures. Revision checks and
`ProjectIncidentReceipt` make mutations idempotent. Reusing a key for a different request fails.

The Producer handles `ProjectHealthEvents.ReviewDue` independently of its planning callback and
inherits `CSweetManagerAgentBase`, which uses `ProjectIncidentReview` for deterministic diagnosis. It does not send diagnostics to an LLM.
Reports distinguish facts, likely causes, missing evidence, and recommended action. If the detailed
read is unavailable, the baseline still reaches management. Unknown root causes remain unknown.
The targeted event is a separate durable inbox commitment. Failed diagnostic attempts and event
delivery errors append evidence to the existing incident without waking the failing path recursively
or extending its handoff deadline. Execution failures retain the exact affected ticket; a failed
ticket caused by that execution is coalesced into the same incident.

An escalation report and its next handoff commit together. Creative Director and Chief of Staff handle incident
events before ordinary intake routing. They forward operational problems outside their responsibility
without granting repairs or changing scope. Attention callbacks rediscover pending incidents.

Every agent hop has a persisted 15-minute deadline. Delivery acknowledgement does not extend it.
An offline Producer also has a deadline: its baseline moves up the chain even without diagnosis.
Routing reads the current hierarchy, detects cycles and missing relationships, and falls back to the
active human organization owner. If no owner exists, delivery stays pending. Human recipients have
no automatic escalation deadline.

`ProjectIncidentDelivery` is a transactional human-delivery outbox. It writes native Communications
messages and notifications using stable receipts; ordinary management check-ins are not required.
Incident IDs, evidence, requested action and handoff history remain attached. Recovery is based on
authoritative completion/cancellation evidence, never acknowledgement. A recurrence gets a new ID.

## Diagnostic authorization

`Platform.ProjectHealth` exposes typed health/incident reads, bounded diagnostics, reports and
forwarding. `ProjectHealthCapabilityHandler` rechecks current installation grants. Access also
requires current monitoring-manager assignment or current reporting ancestry for the incident. The existing
human-only `EmployeeAuditAccess` policy remains unchanged.

Diagnostic queries include only exactly correlated work/attempt/model/tool records, using sanitized
source snapshots and verified audit payloads. `McpGatewayEndpoints.CurrentCapabilityAttemptAsync`
attributes capability evidence only when the authenticated runtime has exactly one live attempt;
ambiguous calls remain uncorrelated. Schema validation failures are captured within the audited call.
Unrelated chats and runtime tails are excluded. Reads are audited, paginated and bounded; missing,
preview-only, truncated or inaccessible evidence is not reconstructed.

## Deployment and verification

1. Deploy the platform and apply the additive `ProducerProjectHealth` and `ManagerIncidentReviews`
   migrations through the normal migrator. The first adds five tables; the second adds assessment
   disposition, recovery-action reference, follow-up time and its deadline index.
2. Publish SDK 3.58.0, then Producer 2.14.0, Product Manager 2.19.0, Creative Director 1.13.0, and Chief of Staff 2.10.0.
   Review the additional incident capabilities and event subscriptions on installed revisions.
3. Restart AgentHost for the worker and new broker handlers. No repair, restart, or retry authority
   is added to agents. Existing recovery policies remain active.

`ProjectHealthTests` covers the threshold, leases, waits, independent failures, hierarchy changes,
dedupe, idempotency, access, recovery and native delivery. `ProjectHealthPostgresTests` verifies
migrations, transaction rollback, unique open episodes and optimistic concurrency against a disposable
loopback database when `CSWEET_COMPUTE_TEST_DATABASE` is configured. SDK and agent incident tests use
`AgentTestRuntime` and require neither credentials nor a model.

Operational audit events are `project-health.incident.changed` and `project-health.diagnostics.read`.
Pending incidents, `EscalateAt`, delivery receipts, and worker errors identify stalled notification paths.

### Implementation validation

- Affected platform regression run: 142 passed, including migration, rollback, uniqueness,
  concurrency and model-snapshot checks on a disposable PostgreSQL 18.3 container. The container
  and temporary volume were removed afterward.
- SDK: 283 tests passed, plus two HelloAgent sample tests. Generated authoring-template tests
  passed (seven), along with its self-test.
- Package-only agent suites: Producer 206, Creative Director 112, Chief of Staff 71, and Product
  Manager 73 passed. All four agent self-tests passed; sibling SDK references were disabled.
- Packed and inspected NuGet metadata: SDK 3.58.0, Producer 2.14.0, Creative Director 1.13.0,
  Chief of Staff 2.10.0, and Product Manager 2.19.0. Manifest versions and release notes match.
- The earlier broad platform run had six failures outside this feature: the existing five-versus-six
  personal-board column assertion, two UI source assertions, and three loopback tests whose existing
  server-address reads use `Single()` when the test host exposes multiple addresses. These unrelated
  behaviors have not been changed by the manager generalization.

Migrations have been exercised on disposable databases, not applied to the running application.
Packages have been built locally, not published or installed. Platform-first deployment and installation
grant review remain rollout steps.

## Generic manager ownership and assessment

The JSON `rolePolicy.baseType` is `manager` for Product Manager, Video Game Producer, Creative
Director and Chief of Staff. `profile: manager.v1` supplies the matching policy. Specialized role keys
and skill preferences remain independent. Import and SDK validation reject conflicting declarations;
legacy manifests infer the base family from their policy. This type grants neither project access nor
recovery authority.

`ProjectHealthService.MonitoringManagerAsync` has no job-name checks. It considers active assignments
in order: explicit `project-health-manager` supervision, project-board manager, other supervisor, and
accountable manager. It selects one manager-typed agent as the project's monitoring owner; explicit
supervision resolves ambiguity when several managers oversee the project. Current reporting ancestry
and approved grants determine diagnostic access and escalation.

`CSweetManagerAgentBase` seals incident/attention entrypoints and exposes specialized workflow hooks.
`AssessIncidentAsync` receives authoritative incident data, bounded diagnostics and a stable action
key. It may call existing authorized operations and report `Investigating`, `AwaitingRecovery`, or
`Escalate`. The first two require a follow-up before the unchanged 15-minute escalation deadline;
`AwaitingRecovery` also requires an action reference. `ProjectIncident.ReviewAt` is durable. Due
reviews issue targeted wake hints, while discovery excludes scheduled reviews until the scheduler
makes them actionable. Fresh material evidence triggers reassessment. A recovery claim never closes
an incident: authoritative source evidence still determines resolution.

The generic SDK accessor is `ManagementIncident.MonitoringManagerEmployeeId`. The legacy
`ProducerEmployeeId` wire/storage name remains for compatibility. The old explicit helper remains
available, but new agents should inherit the manager base. See the SDK's `docs/manager-agents.md`
for the manifest and subclass recipe.
