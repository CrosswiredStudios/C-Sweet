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

`ProjectHealthReader.ObserveAsync` also distinguishes completed reviewer inbox work from the
authoritative `WorkStageExecution` outcome. Reporting a blocked quality review completes the inbox
task, but leaves its stage incident open. `ProjectHealthService.ReviewAsync` gives current failures
precedence over recovery hints, preventing a resolve/recreate loop and preserving escalation deadlines.
Stage reports in `ProjectHealthService.PresentAsync` name the ticket, recorded blocker and manager's
next step. `DeliverAsync` dismisses previous incident alerts when recovery is confirmed in the same
save as its delivery receipt; recovery before the first human report produces no cleared-blocker message.
Attention notifications use Important severity, recovery notifications use Routine, and
`AppRealtimeCoordinator.ShowNotification` keeps Routine project recovery in the notification inbox,
expires project attention snackbars after six seconds and ignores replayed notification IDs.

`ProjectHealthTests.FinishedReviewerInboxWorkDoesNotResolveBlockedStageOrRepeatOwnerAlerts` covers
the October 12 Prism Break blocked-review loop, unchanged deadlines, repeated human delivery,
specific recovery guidance and actual stage completion. `NotificationSnackbarTests` covers event
replay after closing and quiet recovery.
Validation on October 12: 60 health/snackbar tests and two disposable PostgreSQL tests passed,
including atomic notification dismissal and realtime updates. AgentHost, API and the browser app
were rebuilt and restarted locally. The live monitor advanced its review revision across multiple
cycles while the affected stage remained one open incident and its human-message count stayed fixed.

## Blocker destinations

`ProjectHealthService.ResolveWorkItemDestinationAsync` in `ProjectHealthService.Links.cs`
resolves the incident's exact affected item (or legacy ticket, stage, or dependency-wait source)
against its current organization, project, board membership, and personal-board owner. Personal
items open the employee's Personal Board with `item` selected; standard-board items open the board's
ticket drawer. Missing, archived, or unrelated records retain the broader investigation fallback.
Chat and collaboration failures retain their exact conversation destination even when an item
supplies project attribution. Destination pages continue enforcing their own read permissions.

`ProjectHealthService.PresentAsync` supplies this destination to newly delivered Communications
reports and `ReadAttentionAsync`. Overview receives `InvestigateUri`/`InvestigateLabel` separately
from the manager-report `ActionUri`; `ProjectIncidentAttentionCard` renders both actions. It uses
“Open affected item” if the recorded item is no longer blocked, rather than asserting stale state.
Already persisted chat messages retain their original text and links; later incident deliveries
use the corrected presentation. Rebuild/restart API and AgentHost and reload the browser app for
rollout. No schema migration, agent-package upgrade, or expanded grants are required.

`ProjectHealthTests.OwnerReportAndOverviewOpenTheSameExactBlockedItem` covers personal and standard
boards, delivery/Overview consistency, and current destinations after a transfer. Legacy source
and invalid destination tests cover stage/dependency recovery and tenancy/project boundaries.
`ProjectIncidentAttentionCardTests.ItemInvestigationAndManagerReportRemainSeparateActions` verifies
the rendered ticket and report actions.

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

## Stalled delivery and Overview attention

`ProjectHealthReader.ObserveAsync` uses PostgreSQL JSON containment for the exact
`PersonalWorkContextJson.workstreamId` property, then checks the canonical board/project
association. Applying string `Contains` to this jsonb column generated an invalid
`jsonb LIKE` query and prevented every assessment from reaching incident detection.
The October 12 Prism Break incident exposed this: the technical-planning collaboration
had failed with `llm.request_failed`, while recurring commitments continued reporting
that planning was pending and no project incident existed.

`ProjectHealthService.RecordAssessmentFailureAsync` creates one durable
`health-assessment` incident when an assigned project's assessment throws, including
before any work incident exists. Repeated failures retain the same episode and handoff
deadline. It follows the ordinary reporting chain and human delivery/Overview path.
A successful assessment resolves only that monitoring failure; remaining work failures
retain their own incidents. The report identifies unavailable evidence without claiming
that a failed assessment proved work recovery.

`ProjectHealthPostgresTests.JsonScopedWorkAndFailedPlanningReachDirectorThenOwnerAttention`
exercises the actual PostgreSQL query, failed coordination despite scheduled waiting work,
personal-task scope filtering, deadline routing, director-authored native messages,
attention visibility and authoritative recovery. `ProjectHealthTests.BrokenAssessmentEscalatesOnceAndOnlySuccessfulAssessmentResolvesIt`
covers repeated assessment failures and recovery.

Validation: 71 targeted health/dashboard/widget tests and both PostgreSQL tests passed
in `StallEscalationVerified`. PostgreSQL verification migrated fresh randomly named
`project_health_test_*` databases and removed them afterward. Rebuild and restart
AgentHost to activate the fix; no schema or agent-package upgrade is required. The
live planning collaboration still requires recovery of its provider failure through
the existing authorized workflow; these checks did not retry it or mutate live data.

`ProjectHealthReader.ObserveDeliveryPlansAsync` compares Draft aggregate planning pins, child membership and child planning revisions against current project-board records. A stale draft produces one `delivery-plan:<id>` incident even if a manager labels recovery Running, schedules another review or performs other model work. `CSweetDbContext.CaptureProjectHealthSignals` records delivery status/scope changes atomically to wake health review. Matching authoritative pins or terminal cancellation/completion resolve the episode; an unchanged retry or acknowledgement does not.

`ProjectHealthService.NotifyAsync` and `DeliverAsync` enqueue `AppRealtimeEvents.ProjectIncidentChanged` with state/receipt updates. The event targets the current recipient and prior human report recipients, and contains only identity/revision wake hints. `ReadAttentionAsync` checks current organization membership, recipient routing and incident access; only delivered Open reports appear in `ApprovalDashboardResponse.ProjectIncidents`. `CommandCenter` refreshes on this event and reconnect. `PendingAgentQuestions` counts these reports and links native Communications via “Read manager report”; these are separate from approval decisions. Reports disappear after authoritative recovery, without conflating unread or dismissed notifications with incident resolution.

## Interrupted inference and concise recovery reports (2026-10-12 Pacific)

The Prism Break technical-planning failure at 17:41 Pacific was an interrupted DeepSeek
`deepseek-flash` stream after approximately 21 minutes 38 seconds. The retained AgentHost
exception was `IOException` wrapping `SocketException (10054)`: the remote side closed the
transport connection. This proves a transport interruption; it does not distinguish a provider
failure from an intermediary/network reset. The persisted `llm.request_failed;retryable=false`
classification hid that recoverable cause. The request allowed 128,000 output tokens including
reasoning; that permissive ceiling contributed to lengthy requests but is not proof of why
the connection reset.

There were additional reporting defects: clearing `CurrentAgentWorkItemId` separated the failed
delivery from its collaboration, creating two incidents for one failure. Repeated manager handoffs
appended diagnostic text, including model chunks, into long reports. A newest-200 audit window
could contain only stream chunks and omit the actual failure.

- `LlmProviderFailureMessage.IsTransient` recognizes connection-reset IO wrappers and interrupted
  HTTP response streams, while ordinary filesystem errors and permission failures remain permanent.
- `AgentCoordinationService.RecoverTransientFailuresAsync` admits structured retryable provider
  failures, uses the exact failed work/model-run association, and caps this recovery at three
  total execution attempts with cooldown. Current provider, installation and participant checks
  still apply. It resumes the existing speaker/transcript through the authorized workflow.
- `ProjectHealthReader.ObserveAsync` correlates deliveries using the immutable session ID and
  retains one collaboration incident across retries and cleared current-work pointers. An unrelated
  completed originating ticket or completed turn does not prove the collaboration completed.
- `ProjectHealthService.CoalesceCoordinationIncidentsAsync` consolidates legacy duplicate work
  incidents during assessment. Duplicate episodes become `Superseded`, retain their audit history,
  stop pending delivery, and leave Overview attention. Consolidation does not claim recovery.
- `ProjectHealthService.PresentAsync` builds bounded current-state reports for native owner messages
  and Overview: what failed, recovery status, and the next action with a collaboration link.
  Raw diagnostics and escalation history stay out of the human summary. Unknown historical
  `llm.request_failed` errors explicitly retain uncertainty about their cause.
- `ProjectHealthService.DeliverAsync` appends an owner message only when the substantive report changes;
  evidence-only revisions still receive durable receipts. Existing messages remain immutable.
  Delivery, notifications, receipts and realtime wake hints commit together.
- `ProjectHealthService.ReadDiagnosticsAsync` prioritizes typed model-run summaries and failed/denied
  audit records, excluding stream chunks before the bounded query window.

Validation: all 150 targeted platform tests passed, including both disposable PostgreSQL tests.
Victor's companion release `CSweet.Agent.TechnicalDirector.VideoGame` 2.16.1 passed 137 package-only
tests and its self-test; its packed NuGet version was inspected. That release adds concise prompts
and task-specific output ceilings while retaining distinct acceptance criteria and validated JSON.

Rollout: rebuild/restart the platform services and upgrade Victor to 2.16.1. No database migration
or Naomi package update is needed. After rollout, use **Retry collaboration** on the existing
failed planning session; restarting alone does not reinterpret the old non-retryable saved error.
No message to an individual agent is required. These checks did not deploy changes, change live
provider settings, or retry the live collaboration. New classified transient failures use bounded
automatic recovery; unresolved incidents retain the manager-to-owner escalation and Overview path.

### Overview collaboration actions

`ProjectHealthService.ReadAttentionAsync` adds current session identity, revision, status and
manager retry eligibility to `ProjectIncidentAttentionResponse.Collaboration`. Items still follow
the management escalation/delivered-report path; every transient failure does not immediately
interrupt the owner. Current organization/project access is rechecked on each read.

`ProjectIncidentAttentionCard` shows **Retry** and **Investigate** for retryable failed/blocked
collaborations. Retry uses the existing Communications manager retry endpoint with the exact
session revision and the same stable key used in Communications. The endpoint remains authoritative
for permissions, participant readiness and concurrency. Duplicate clicks are suppressed, queued
recovery stays visible, and stale-state or permission errors use concise text without raw server logs.
Other project incidents retain **Read manager report**.

Investigate opens Communications with `perspective` and `collaboration` query parameters.
`Communications.CollaborationId` retains that specific session even if it has since completed or
been cancelled; `focusCollaboration` scrolls/focuses its card instead of initially scrolling to the
bottom of the transcript. Overview refreshes after retry and through existing incident/reconnect
events. Recovery confirmation, rather than the retry click, removes the attention item.

Validation: 144 targeted health, Overview, retry-interaction and Communications tests passed in
`StallEscalationVerified`. A JavaScript check verified focus/scroll behavior and a missing-target
no-op. The local build is verified; the running application still needs a rebuild/restart.

## Prismatic Break recovery diagnosis (2026-10-11 Pacific)

The Victor Lin / Gabriel Reyes technical decomposition collaboration generated four successful sessions for the same accepted planning fingerprint as three continuation decisions arrived. The provider calls consumed approximately 23 minutes and 53,039 output tokens. The delivery draft remained pinned to earlier aggregate/task planning revisions after the canonical backlog changed. Workflow and sprint-preflight commitments repeatedly scheduled the same scope conflict as Running; no sprint execution had started at inspection. The previous health reader did not compare delivery-plan pins, and Overview consumed questions/approvals/hiring only.

Update platform code and Producer 2.19.8 (Agent SDK 3.64.2) together. Allow the durable recovery callback to reread current accepted planning and reconcile the existing draft. If the retained commitment still waits, the reporting manager may issue `Retry staffing: updated draft reconciliation` through the authorized manager chat path. Do not delete the plan, recreate tickets, fabricate estimates, bypass independent QA, or directly edit persisted revisions. Finish configuration, each affected task finalization and authoritative preflight as separate resumable steps. A plan activation does not itself establish that a sprint has started; verify the sprint execution record after preflight succeeds.
### Recovery activity without sprint progress

`CSweet.AppHost/Program.cs` connects `agentHost` to GitHost with the trusted service key and `GitHostBaseUrl`. `CSweet.AgentHost.Broker.WorkDeliveryCapabilityHandler` executes delivery tools using its local `IWorkDeliveryService`; ExecutionGateway handles VM ingress. API, WorkerHost or ExecutionGateway connections alone do not supply AgentHost's configuration. A missing connection selects `UnavailableTrustedSourceControlHostClient`, causing delivery activation to return `Delivery branch operations are unavailable.`

`HierarchicalProjectDelivery.Same` compares assignments by `StageKey` and ignores `SelectionEvidence.SelectedAt`. Database navigation collection order must not trigger task finalization or a new board event. `SpecialistAgent.EnsureCommitmentAsync` preserves future `PersonalTodoWaitState.NextReviewAt` deadlines for both workflow recovery and sprint preflight; the durable due-review event resumes the work. An explicit authorized staffing retry can still wake waiting commitments after a repair.

`ProjectHealthReader.ReadAsync` recognizes both direct `Delivery planning needs attention:` waits and Producer-wrapped `Workflow staffing needs attention:` waits. A scheduled retry does not hide a recorded delivery failure or move its incident escalation deadline. These callbacks reconcile authoritative state using typed capabilities, so they need not perform model inference.

### Cleared dependencies and interrupted direct chats

Producer 2.20.1 `SpecialistAgent.EnsurePlanningCommitmentAsync` re-reads the authorized project roster on staffing events and attention/reconnect discovery. A ready Technical Director releases only the matching initial hiring wait, with the same project, team and planning fingerprint. `EnsureCommitmentAsync` preserves other timed recovery deadlines and uses the existing revision-checked requeue endpoint. Duplicate delivery cannot create another planning commitment or grant execution authority.

`ProjectHealthReader.ObserveDependencyWaitsAsync` checks project/team-scoped hiring requests, fulfillment employees and current installation readiness, exact decided project decisions, and completed planning collaborations bound to the initiator's source fingerprint. A wait that continues for 15 minutes after its authoritative dependency clears creates one `dependency-wait:<task>` incident. Deferral timestamps and unrelated progress do not reset that grace period or the incident's escalation deadline. Releasing the wait or completing/cancelling its work resolves the episode. Unresolved hiring and uncorrelated collaboration evidence never count as cleared dependencies.

`ProjectChatTurnAttribution.RecordAsync` retains the project scope of a successful `WorkstreamGovernanceCapabilityHandler` project read only when the authenticated runtime has exactly one live chat delivery. Ambiguous, expired and foreign-organization deliveries remain uncorrelated. This writes a non-progress `ChatTurnProjectScope` health signal; failure signals retain that association in the same transaction as the interrupted turn.

`ProjectHealthReader.ReadLinkedChatTurnIdsAsync` also discovers exact coordination/intake source turns, canonical source messages and authenticated task-focus records. Direct conversations stay unscoped. `CSweetDbContext.IsInterruptedHealthTurn` includes `CompletedWithWarnings` with `memory.runtime_reset`, while ordinary completion warnings remain successful. Bounded, same-conversation/recipient retry ancestry preserves scope and confirms recovery across repeated interrupted attempts. No automatic replay of partially completed actions is introduced.

These incidents use existing management escalation, delivered owner reports, realtime hints and Overview attention. `ProjectHealthService.PresentAsync` gives cleared-dependency and memory-reset incidents concise explanations and links the affected conversation. Overview continues to show delivered incidents routed to the current human recipient.

Rollout: rebuild/restart API and AgentHost and upgrade the installed Producer to 2.20.1. No schema migration, SDK/package-contract change or expanded agent grant is required. Historical direct turns can be recovered through retained exact source links; attribution is never guessed from an employee's project or timestamps.

Validation in `DependencyWakeVerified`: 104 platform health, Overview, governance/gateway, coordination-wake and memory-reset regression tests passed, including both disposable PostgreSQL tests. The database scenario verifies direct-chat attribution, a cleared decision despite a future review deadline, management escalation, delivered Overview attention and successful retry recovery. Producer's 339 package-only tests and self-test passed. NuGet 2.20.1 metadata and the Linux x64 `.csab` were built and inspected; the bundle verifier passed. No running service restart or installed-agent upgrade was performed during validation.


## Shared SDK manager reports and recovery choices (2026-10-14 Pacific)

SDK 4.1.0 `CSweetManagerAgentBase`, `ProjectIncidentReview` and `ManagerIncidentCoordinator` own report analysis and resolution dispatch for every manager role. Generation/execution hooks allow specialization; reusable coordinator methods support other callback implementations. Analysis uses the configured provider, at most 2,048 output tokens and two minutes, with a current-facts fallback.

`ProjectHealthService.CurrentIncidentStateAsync` reads current affected work, bounded decisions, operation state and exact document references. Its hash binds reports and choices to source revisions. `ReportAsync` validates presentation before recording receipts. `UpdatePresentationAsync` rechecks reporting authority and persists reports/outcomes without changing routing, deadlines or incident resolution. The additive `ManagerIncidentOwnerReports` migration stores report JSON, author, report revision, outcome and refresh-discovery timestamp. `RefreshOwnerReportsAsync` issues bounded durable wakes rather than agent polling.

`DeliverAsync` commits the report message, incident-bound executive decision, notification, delivery receipt and realtime records in one transaction, with PostgreSQL delivery/incident locks. `ExecutiveDecisionService` scopes supersession to each incident, rechecks source/grants, locks concurrent answers and starts one durable turn. Busy managers leave choices pending. Intended outcomes are persisted and included in the answer message for authorized delegation.

`MemoryRecallDispatchEvidence.CaptureMetadataAsync` derives the incident marker and project context from the persisted answer and retry lineage. SDK dispatch intercepts it before delegation classification or specialized creative intake. Mutation keys bind to decision/target identity. Canonical manager commitments retain source/project bindings and run before specialist claim policies/task handlers. Outcomes can produce new incident-bound choices for another attempt or Something Else; changed evidence requires a fresh report. Retries replay their saved outcome. Only authoritative work recovery clears the incident.

`ProjectIncidentAttentionCard` uses reporting employee identity for the installed portrait and shows the complete report. `IncidentResolutionChoices` supplies the same menu, recommendations and inline Something Else textarea in Overview and Communications. `OverviewAttentionItems.StandaloneQuestions` and pending-question discovery prevent duplicate cards/counts.

Verified releases: SDK 4.1.0; Naomi 2.1.0; Producer 3.1.0; Product Manager 3.1.0; Chief of Staff 2.13.0. Manifest, implementation/package versions and release notes match. Package-only suites passed: SDK 403 plus two sample tests, Naomi 175, Producer 353, Product Manager 73, Chief 83. Generated-template tests (seven) and all manager self-tests passed. All five NuGet package versions/dependencies were inspected.

Platform validation: 143 affected regressions passed, including three disposable PostgreSQL tests covering migration/model parity, forced delivery rollback and concurrent answers. UI tests cover inline/custom answers, retry keys, outdated choices, portrait identity and deduplication. The platform used packaged SDK/WorkManagement/Office/Isolation dependencies; its existing sibling Memory libraries remained because current Memory provider packages are absent from the local feed. Tests did not migrate or recover the live Prism Break project.

Rollout: deploy/restart the platform and apply `ManagerIncidentOwnerReports` first, then import the manager releases and review `platform.management.incident.presentation.update.v1`, `platform.management.incident.resolution.read.v1` and `com.csweet.management.incident.owner-report-requested.v1`. Declarations do not grant permissions. Approved installations refresh existing incidents through discovery. Earlier conversation messages remain immutable.

`ProjectHealthService.DeliverAsync` compares reports without the dynamic source-freshness notice. A retry or new diagnostic evidence can make choices outdated and refresh Overview without repeating the same manager report or creating another notification. Substantive findings and confirmed recovery still append updates; historical messages remain unchanged.

`ProjectHealthService.CloseIncidentQuestionsAsync` cancels only unanswered questions bound to a resolved or superseded incident. `ReconcileClosedIncidentQuestionsAsync` repairs legacy closed-incident questions through bounded discovery using correlated report messages and exact persisted incident bindings. Incident and decision changes publish their durable realtime records through `SaveChangesAsync`; answered choices and unrelated questions retain their history.

`AppRealtimeCoordinator.ShowNotification` suppresses every ProjectHealth snackbar; reports remain in Overview, Communications and the notification inbox. `ApprovalState.OnRealtimeEvent` reloads on ProjectIncidentChanged. `CommandCenter` exposes **Refresh attention items** to recover a missed update without dismissing an unresolved incident. `IncidentResolutionChoices` renders cancelled questions without actions and explains that no response is needed.
