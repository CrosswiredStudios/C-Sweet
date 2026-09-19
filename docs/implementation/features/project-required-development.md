# Project-required development

## Authoritative boundary

`ProjectWorkPolicy` reads the installed package's `rolePolicy.requiresProject`. The optional SDK boolean defaults to false for compatibility and is independent of role names. Software Developer 1.11.0 opts in. It grants no additional authority. `RequireAsync` checks an active/approved Workstream, explicit ProjectParticipant, current project/team association, current team membership, an active employee and current scoped board-read grant. Each operation still checks its own capability and action grants.

`ProjectCapabilityPolicy`, `AgentWorkInbox`, `WorkOrchestrator`, `WorkItemMutationEngine`, `SoftwareDevelopmentWorkService`, workspace brokers and compute dispatch repeat the prerequisite at their authority boundaries. Conversation/configuration and ordinary personal reminders remain available before setup. A request to skip setup cannot authorize delivery.

## Human setup

`ProjectSetupEndpoints` exposes authenticated human-only create, membership and status operations under `/api/core/organizations/{organizationId}/project-setup`. `ProjectSetupService` shares participant, team, board, grant and manager-capacity provisioning through `ProjectSetupService.ProvisionApprovedAsync`, called by `WorkstreamManagedActionExecutor.CreateAsync`. The governed executor still runs only after the existing managed-action approval and staffing checks. Human setup does not impersonate an agent or manufacture an agent proposal.

`ProjectSetup.razor` supports independent creation and an opaque `intake` query ID. GET only loads a draft. Current identity and current revision are required on submission. Name, goal, manager, explicit members and a resolved team are required; repository selection is optional. Existing agent teams are reused. Conflicting lifetime memberships are rejected, never moved. Reporting relationships and existing team leadership are unchanged.

The built-in `SoftwarePrototypeProfile` supports a human manager plus a developer without installing a product-manager package. Project boards have To Do, Doing, Testing, Blocked, Done and Cancelled columns. `PersonalTodoService.ListAsync` projects canonical project tickets into a single employee view; each item retains its real ticket and board IDs. Personal reminders remain separate stored work.

## Durable intake and resumption

`ProjectIntake` retains the actual human source message, chat turn, requesting human, assigned contributor, normalized request, ticket-ownership choice, setup selection, revision and handoff references. It is not a ticket. States are AwaitingProjectChoice, AwaitingProjectCreation, AwaitingAssignment, AwaitingManagerAssistance, Ready, Started and Cancelled.

`PlatformProjectClient` exposes typed retention, read/discovery, choice, setup-link results, manager assistance and start operations. Developer capabilities are scoped to its own intake records. Chief and manager discovery are separately authorized. Changed events are wake hints: agents reread current state. Activation/attention recovery performs bounded discovery. Source messages from another human or another agent conversation cannot change an intake.

`StartProjectIntakeAsync` creates one canonical project-board request after project readiness and explicit agent-ticket ownership. The SDK claims it into Doing before developer planning. Replays retain the same root and plan IDs. Human ticket ownership remains pending rather than silently switching to agent-created tickets. Manually authored delivery tickets use the existing governed assignment/orchestration interfaces and the same project boundary.

A changed project choice cancels obsolete coordination and withdraws pending hiring recommendations. It releases unbound manager reservations. Approval proposals bind the intake's setup-choice message ID, so an earlier proposal cannot authorize a later choice. Opening or cancelling a form creates no project or assignment. Removing the developer from a submitted form leaves the intake AwaitingAssignment.

## Chief and manager coordination

`ProjectIntakeService.Staffing` resolves the active `chief-of-staff` leadership assignment, checks active installation grants, and starts a typed `project-manager-assistance.v1` coordination session with SourceIntakeId. Payloads include the retained request and success criteria rather than unrestricted conversation history.

The Chief discovers compatible active software product managers with no active project and reserves one, or creates a stable hiring recommendation through the existing approval/fulfillment flow. A unique ProjectManagerReservation plus the organization transaction lock serializes competing setup requests. Humans do not consume that capacity. Completion/cancellation releases capacity; reopening checks it again. The selected manager receives `project-manager-setup.v1`, uses its existing staffing approvals, then submits a governed workstream proposal. Hiring or a coordination response never marks development ready.

Missing/unavailable Chief access leaves an actionable intake status and manual setup URL. A reserved manager is not silently replaced on reconnect. Cancellation never fires an employee or reverses a completed hire.

## Delivery and repository continuity

`ProjectDeliveryBinding` owns the board, team and optional repository for the actual Workstream. Workspace preparation reserves a stable project repository identity and reuses it for subsequent epics and repairs. Existing repositories selected during human setup remain bound to the project. Titles are display text, not repository identity.

Task branches and `TaskDeliveryService` still control Testing, optional assigned QA, exact task approval, story/epic merge preferences and merging. Approval uses the project's manager and does not change the developer's organizational manager. Membership/grant revocation blocks further planning, source operations, compute dispatch and merges. Deployment still requires verified running-review URL evidence before completion.

## Rollout and verification

Apply `RequireProjectIntake`, `LegacyProjectComputeRecovery` and `RetainQueuedProjectWork` with the platform upgrade. The first migration installs the built-in profile and snapshots persisted execution evidence exactly once. Existing descendants and continuation of those started plans retain their authorization. New requests and follow-up epics do not inherit it. Existing compute exceptions apply only to captured environments while matching legacy work remains unfinished.

`RetainUnstartedProjectRequestsAsync` converts queued chat-backed development requests without execution evidence into durable intake, blocks them with a setup link, and reuses their existing ticket/hierarchy after authorized setup. No new projectless execution is grandfathered merely because it was queued.

Upgrade SDK to 3.51.0, Developer to 1.11.0, Chief to 2.7.0 and Product Manager to 2.18.0. Review/install their changed capability and event grants. Build platform and agent packages together; old running processes do not acquire these changes until restarted/upgraded. The implementation does not alter the currently running database or agents during development verification.

`ProjectSetupTests` covers source authorization, duplicate/stale submissions, explicit participants, team compatibility, assignment removal, current grants, ticket ownership, canonical planning, queued rollout, missing Chief and manager capacity. `TaskDeliveryTests` covers task merging under project management. SDK tests cover manifest opt-in and typed intake contracts. Package-only builds disable sibling SDK/contracts references; PostgreSQL concurrency and live agent/browser delivery still require the configured integration environment.


### Verification recorded on 2026-09-18

- Package-only focused platform suite: 146 passed, 1 PostgreSQL-dependent test skipped. Includes actual form rendering without writes, Chief staffing/reservation/cancellation, canonical tickets and blocked notifications, direct-capability prerequisite checks, repository reuse across renamed repair epics, task review, and migration/model consistency.
- SDK: 248 tests passed; HelloAgent sample: 2 passed. A fresh temporary agent generated from the template passed 7 tests and its self-test using the packaged SDK.
- Package-only agents: Developer 122, Chief 62, Product Manager 72 tests passed. Developer manifest self-test passed.
- Packed and inspected NuGet metadata: SDK 3.51.0, Developer 1.11.0, Chief 2.7.0, Product Manager 2.18.0. All agent release-note headings match their manifests.
- Full platform suite: 2,543 passed, 21 skipped, 7 failed (before the final four direct-capability cases were added to the focused suite). Failures are the existing Projects/CommandCenter source-string assertions, the empty-manifest AgentBuildSummary fixture, CoreService's five-column expectation (the existing board has six), and LocalWebPreviewServer/LocalWebPreviewWorker/OfficeConnectionIdentity local-host tests. Their affected behavior was not changed by this feature.
- PostgreSQL concurrent transaction behavior and a live complete prototype delivery have not been exercised. No migrations, package upgrades, or restarts were applied to the running instance.
