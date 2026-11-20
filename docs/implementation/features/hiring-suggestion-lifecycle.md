# Hiring suggestion (suggested action) lifecycle

This is the authoritative reference for the "HIRING PLAN / Hiring suggestions" widget that appears in
Communications when an agent attaches a Marketplace action to a message or chat turn, and for the
company overview's hiring row.

## Dependency waits, card errors and retry

`HiringService.DependencyRecovery` converts expected repository/configuration failures into durable
outcomes before committing the hiring transaction. `DependencyWaits` stores host-owned workflow
identity, error code, message, attempt count and eligibility time in the indexed
`PluginOperationalState` queue (`host-dependency-wait.v1`). The same transaction records the card
error and realtime outbox hint. A failed preview no longer rolls its recovery evidence back or
becomes an HTTP 500 that consumes the Chief's work-delivery attempts.

Rate limits retain the provider's retry time. Other transient failures back off; invalid credentials,
access denial and configuration errors require review. `AgentHireOperationWorker` performs bounded
due-state discovery through `RecoverNextHiringDependencyAsync`; no model or agent polling loop is
needed. The organization hiring lock serializes recovery and owner retries with dismissal, withdrawal,
manual selection and hire commits. Recovery re-reads the active Chief, recommendation and both
delegation policies. A saved wait never grants authority. Manual selection cancels its wait immediately.

`AutomaticHiringControl` is shared by Overview and Communications. It shows the actual error as an
error alert, the automatic retry time when present, and **Retry hiring**. The recommendation-scoped
`HiringEndpoints` retry route requires an owner, reuses the existing operation/slot identity, respects
an outstanding provider cooldown and preserves manual-selection overrides. Fulfilled retries return
the completed recommendation rather than a misleading 404. Review-required waits resume only after
an explicit retry; they do not repeatedly fail in background callbacks.

`RepositoryMetadataCacheHandler` caches only bounded GitHub repository metadata, partitioned by the
credential identity. It coalesces concurrent reads, observes provider cooldowns across repositories,
retains commit-pinned files for longer than mutable refs, and revalidates mutable facts with ETags.
Errors and release payload downloads are not cached. Changing credentials creates a separate partition.
Approval, package/grant checks and current hiring authority are never cached by this handler.

`ValidateCatalogPreview` permits network requests to reach the existing immutable hiring review.
`AgentHireDialog` prominently discloses public-web access before approval. Delegated confirmation
still enforces both saved and live permission limits, and owner confirmation still binds the exact
reviewed package and network grants. The former blanket `all-public` rejection prevented owners
from opening the review it told them to perform.

Regression map: `HiringDependencyRecoveryTests`, `HiringAutonomyPostgresTests`,
`RepositoryMetadataCacheTests`, `OverviewHiringCandidateTests` and existing catalog-access tests.
The November 16 Qwen Games incident involved an anonymous GitHub allowance of 60/60 exhausted
requests, repeated Chief recovery failures, a rolled-back error card, and then the independent
public-web preview rejection. These are reusable dependency/review paths; no game or employee
identity controls their behavior.

## Employee dismissal

`OrganizationUserService.DeleteAsync` calls `EmployeeActionExpiration.ExpireAsync` before
detaching the installation. Dismissal and expiration share the organization's hiring lock
and one transaction, including the durable realtime outbox records. Pending recommendations,
linked owner previews and delegated hiring workflows, unfinished hire operations, suggested
actions, executive questions, managed actions, resource-change and replenishment requests,
document-access requests, source-control approvals and their waiting operations, and prepared
connector actions become **Cancelled**. Passive Chief audit cards also settle as cancelled.
Completed hires, answers, artifacts and historical records remain available.

Dismissal also ends the employee's open `LeadershipAssignment` records in the same
transaction. `CoreOrganizationService`, `HiringService.TryAutoAssignChiefOfStaffAsync`,
and `BusinessOnboardingService` treat only active, non-archived employees in the same
organization as current leaders. Assigning a replacement closes stale leadership rows
under the hiring lock before creating the new assignment and onboarding outbox event.

`HiringService.RestoreHiredChiefAsync`, called by `ListRecommendationsAsync`, repairs
legacy missing Chief assignments only for a single enabled Chief employee backed by an
approved hire workflow. It never displaces an active Chief or chooses among multiple
eligible hires. The replacement retains its own installation-scoped hiring policy;
the departed Chief's delegation and answers are historical and are never copied.
Restoration commits an attention invalidation with the assignment so an already-delivered
onboarding event does not leave candidate selection waiting for a later periodic review.
`BusinessOnboardingServiceTests` covers stale-slot replacement, reconciliation,
ambiguous hires, existing active leadership, and fresh hiring-policy setup.

`CurrentActivityService.BaseState` reports an unclaimed personal task with no active
callback or model call as **Waiting for follow-up**. A released task awaiting review
does not imply that an execution lease expired; genuinely expired claims still recover.

`EmployeeActionExpiration.ReconcileAsync` repairs requests left behind by older dismissals
when hiring recommendations, the approvals dashboard or source-control decisions are read.
It selects disabled business installations with no active employee. Marketplace preview and
confirmation also perform reconciliation and check the requester's current availability.
The background hire worker shares the dismissal lock before committing a hire and preserves
cancellation if dismissal occurs while it checks a build. Cancelled hires cannot be retried.
Executive answers and managed approvals reject inactive requesters independently of the UI.

`EmployeeActionExpirationTests` covers cancellation, old-card rejection, historical-record
preservation, repeat dismissal, legacy cleanup, rehire, and dismissal during background work.
`UserActionServiceTests.EmployeeDismissalAndActionExpiration_RollBackTogether` verifies the
transaction and cancellation-event boundary against the opt-in PostgreSQL fixture.

## Hiring autonomy and candidate review

### Cool Games approved-team hiring stall (November 4)

The accepted Prism Break pitch and approved resource-change request existed, but no
Creative Director/Producer recommendations or employees had been created. Chief
`ReconcileApprovedResourceChangeAsync` serialized the SDK's optional
`assignAsProjectManager: false`; `McpToolCatalog` rejected it as an extra property
before `WorkforcePlatformCapabilityHandler` could invoke the hiring service.
The Chief retained `chief.resource_validationfailed` review work, while Marcus's
project state correctly remained `TeamStaffingPending`. An approved team plan
alone does not establish completed hires or a created project.

The `HiringCapabilities.UpsertRecommendation` input schema now accepts that
boolean. `HiringService.UpsertRecommendationCoreAsync` still checks imported
project, Chief, accepted-profile/category, approved-role and team authority when
it is true. `HiringServiceTests.SdkSchema` passes actual SDK-serialized approved
team recommendations through the catalog schema and real service, verifies both
ordinary roles persist, and retains rejection of unauthorized manager assignment,
wrong field types and unknown properties. Legacy requests may omit the field.

Recovery requires rebuilding/restarting AgentHost with the corrected catalog;
the Chief's approved-plan discovery can reconcile the existing request and resolve
its saved review block. Preserve the accepted pitch, approval, team and failed
audit evidence. No new staffing approval or direct database repair is needed.

Verification: 133 focused hiring/autonomy/gateway/SDK-schema tests passed; two
optional PostgreSQL concurrency fixtures were skipped. Results are retained at
`artifacts/dynamic-output/results/hiring-schema.trx`. The normal development
AgentHost build succeeded and the trusted host was restarted through Aspire on
November 4; installed agent packages, approval records and business state were
not edited. The 22:29 UTC recovery pass created both source-linked recommendations
(`creative-director` and `game-producer`); no new capability validation errors were
recorded after restart. By 22:30 UTC both hires were fulfilled: Naomi Chen 4.0.4
and Gabriel Reyes 6.0.5 were active, and Marcus started the normal `Propose the
accepted game project` coordination. Verify the resulting Producer-managed
project separately before treating project setup as recovered.

[Chief hiring autonomy](chief-hiring-autonomy.md) documents the authoritative owner policy,
approval-time delegation, candidate selection, and automatic execution boundary.
`WorkforcePlan.SelectedCatalogAgentJson` and `SelectionRationale` populate recommendation
cards. `HiringSuggestionCarousel` and `Marketplace` both render `AgentHireDialog`, sharing
preview, configuration, grants, cancellation and confirmation logic. A selected candidate
shows **Hire** and **Browse Candidates**; otherwise the action remains **Browse
candidates**. Existing lifecycle states continue to govern action availability.

Marketplace context uses authorized server attribution and remains attached when search
filters are cleared. **Browse outside this plan** explicitly detaches it. Unavailable or
fulfilled recommendations disable contextual hiring. Automatic progress is persisted as
`DelegatedHiring` conversation messages alongside hire-operation outbox events.
Automatic hiring also covers the Chief's own standalone leadership recommendations under
the completed live owner policy. Chief `PrepareHiringCandidateAsync` attaches no manual
marketplace action while an automatic hire succeeds, starts, queues, builds, or completes.
Missing authority or required owner configuration retains the review action.

## Company overview hiring row

### Correcting an approved, unfilled staffing slot

`ApprovedStaffingLineage.ResolveAsync` follows explicit `SupersedesRequestId` links from a hiring
recommendation to the current approved plan while the role remains desired in the same organization
and staffing scope. Team-bound plans follow the existing team-wide approval boundary, including a
shared team serving several projects; a plan without a team must retain its project scope.
Matching role names elsewhere, pending replacements, branches, cycles and a role
removed and later reintroduced do not transfer authority. Discovery reads the connected lineage and
does not depend on the recent-proposal page or an agent polling loop.

The historical `SourceResourceChangeRequestId` remains intact. The Overview receives a separate
`CorrectionSourceRequestId` plus `CorrectionSourceRevision`; the submission binds both, so two plans
with the same revision number cannot silently change the correction target. Owner/current-manager
authorization and active authorship are checked against the resolved current approval. The same
resolver serves action discovery and submission under the hiring lock. A pending correction hides
all related actions, and retries return the originally recorded correction even after replacement
approval removes the disputed role.

Cancellation covers unfinished recommendations for that continuously carried-forward role across
the lineage, including hiring workflows already started from older recommendations. Completed hires
and other roles remain intact. A fully filled current role cannot be corrected through a leftover
unfilled duplicate card. The correction is addressed to the current staffing author and goes through
ordinary proposal and manager approval; this does not infer a replacement role or rewrite old approvals.

The November 14 QFN Games incident retained `game-security-privacy` in a later approved proposal after
Victor was hired, while the visible recommendation still referenced the superseded proposal. The
old action filter hid correction. `StaffingCorrectionLineageTests` reproduces the carried-forward
case, same-number revisions, normal replacement approval, current-manager authority, cancellation,
replay and invalid lineage. `StaffingCorrectionButtonTests` verifies the UI sends the resolved identity.

`StaffingCorrectionService.RequestAsync` implements the Overview **Request correction** action.
It authorizes the owner/accountable manager, checks the source revision, and serializes with hire
commits using `HiringOrganizationLock`. `HiringRecommendationCancellation.CancelAsync` is shared
with ordinary recommendation withdrawal. It cancels remaining hiring effects and suggested actions,
retains completed effects, and settles execution cards. The correction, conversation evidence and
notification outboxes commit together; retries reuse the correction identity.

`ResourceChangeService.ReadForInstallationAsync` returns authorized pending corrections independently
of the recent-request page. A replacement uses `CorrectionId`, the current correction revision and
the exact direction in `staffing-correction` evidence. Unbound/stale proposals cannot approve that
source; fulfilled positions must remain. Approval completes the correction and supersedes the old
plan through the existing hiring path. The original approval is never edited into a different hire.

Producer 6.12.4 creates a separate correction commitment on event/reconnect, reasons about the
approaching work and current roster, and selects named capabilities. Its replacement removes the
disputed unfilled capacity while retaining occupied positions, including unavailable employees.
An unchanged blocked correction stays blocked; a new authorized manager decision can release its
specific wait. Other waits and unaffected work keep their own eligibility.

`StaffingCorrectionResponse` and optional proposal binding are additive SDK 5.29.0 contracts.
Other staffing-author agents adopting this pattern must discover their authorized pending corrections,
retain owner direction, submit a normally reviewed replacement, and continue from the actual roster.
The platform does not infer a replacement role, approve hiring, or impose a game-specific team.

Regression maps: `StaffingCorrectionTests`, `StaffingCorrectionButtonTests`,
Producer `StaffingCorrectionRecoveryTests` and the corrected/fresh kickoff journeys in
`PitchRefinementTests`. Deploy the API/AgentHost and migration before updated agent requests.

`HiringService.WithCurrentCandidateBrandingAsync`, used by both recommendation list paths,
refreshes name, portrait, logo and accent in the response from the embedded first-party catalog
only when the saved candidate's reference and agent ID still match. This repairs cards selected
before branding was added, including Marcus Vale. The saved selection, rationale, price,
capabilities, repository and approval evidence remain unchanged; no external marketplace request
is needed. `MarketplaceAgentPresentation.IsAgentImageUrl` accepts versioned JPG and PNG portraits
inside the bundled agents directory. `OverviewHiringCandidateTests` covers Marcus's PNG on the
actual hiring card; `HiringServiceTests.CandidateBranding` covers stale selection projection and
identity mismatch. These are host changes and require rebuilding/restarting the app and API;
updating an installed agent alone does not update hiring-card presentation.

`HiringRecommendationResponse.NoSuitableAgentFound` exposes the persisted no-eligible-candidate
outcome from `SelectCandidateAsync`, including recommendations created before this UI state.
Both `OverviewHiringCandidateCard` and `HiringSuggestionCarousel` show **No suitable agent
found** with an **Open Agent Marketplace** link bound to the original role and recommendation.
The role remains pending and unfilled, without an interviewing spinner or a fabricated hire.
An unstarted search and explicit owner-selection mode remain distinct. Repeated automatic
attempts reuse the same recommendation and preserve publisher restrictions. Human marketplace
fulfillment is outside this flow.

`OverviewHiringCandidateCard` observes `AgentHireOperationState.Changed` and matches
operations by organization and `AgentHireOperationResponse.RecommendationId`. Starting,
queued, building, completing and uncertain hires disable both **Hire** and **Browse
Candidates**, with an inline live status showing the operation phase and detail. The
optimistic operation retains the recommendation ID before confirmation returns; durable
operation reads retain it after navigation or reconnect. Failed operations release the
card for retry. Preparing a review also guards repeat clicks until the review loads.

`AgentHireOperationState.StartCoreAsync` reconciles a failed confirmation response with
the durable operation before choosing a local result. Explicit confirmation rejections
(`invalid_hire`, `owner_required`, or the endpoint's known conflict codes) show **Hire
rejected** with the server's explanation and release the candidate actions. Retry reuses
the same review and confirmation key; dismissal removes the local notification.
Transport failures, unrecognized error responses and server errors retain **Hire status
unknown** because confirmation may have committed. An authoritative server operation
always takes precedence over either local result.

`NavMenu` shows a count beside **Overview** for all actionable **Needs your attention**
items, including questions, approvals, hiring recommendations, project incidents and compute
access requests. `OverviewAttentionState` reads the existing authorized queue endpoints for
the focused business, refreshes on relevant realtime events and connection/reconnection,
and discards cancelled or late reads after a business switch. Events received during loading
queue another read. `OverviewAttentionItems` shares eligibility rules with `CommandCenter`
and `PendingAgentQuestions`. The badge is hidden at zero, caps its visible label at **99+**,
and marks a truncated compute queue as a lower bound. `AppRealtimeCoordinator` no longer
shows snackbars for `ResourceChangeApproval` notifications; their inbox records remain intact.

For a delegated hire, **Current Activity** links to the Chief’s personal **Hire [role]**
ticket and offers **Choose this role manually**. `CurrentActivityService` shows the ready
ticket while pickup is pending and groups the delegated operation receipt with the same
role on the same personal board. Backlog roles wait their turn. `OverviewAttentionItems`
counts a hiring recommendation when the owner can choose the hire, take over an unmatched
role, or resolve a review or setup problem. A scheduled dependency retry stays with the
activity. Changes to the Chief’s policy or a plan’s delegation refresh the queue and badge.

`CommandCenter` renders one **Needs your attention** queue through `PendingAgentQuestions`
and `AttentionRequestCard`. Pending, decidable `ResourceChange` and `HiringWorkflow`
items from `ApprovalDashboardService` use the **Hiring** card design; other actionable
approvals use **Approval**. The full inbox and sidebar retain their existing counts and
authority rules. Review links supply `approvalId` to
`Approvals`, which puts the actionable selected request first and highlights it. A
completed, unavailable, or non-decidable request produces a notice without expanding access.

`OverviewHiringApprovalCard` uses `AttentionRequestCard.Compact` for the decision row:
the requesting employee's installed portrait, installed role name and requesting team,
a top-right **Hiring** chip, a short team-based title, and **Deny / Review / Approve**.
`ApprovalDashboardItemResponse.RequesterOrganizationUserId` binds portraits by employee
identity even when display names collide. Workflow team names come from the reviewed
`HiringWorkflowApprovalResponse.TeamId`; team-plan names resolve within the organization.
`HiringApprovalPresentation.RequestedHires` counts positive headcount deltas for added,
increased or modified roles, excluding unchanged roles, reductions and removals. The
hire-count button opens a nonmodal popover anchored below the button, using the current
page palette without a backdrop. It supports outside-click and Escape dismissal, close-button
focus restoration, viewport-bounded scrolling, and six role requests per page with quantity,
purpose, timing, reporting context and the complete plan rationale. Zero new hires still
allows review of the full team changes.

Review navigates to `/organizations/{organizationId}/approvals?approvalId={requestId}`.
Approve submits directly from the parent card, without a confirmation dialog. Deny retains
its optional feedback dialog. The details popover contains hire information only; Review
remains on the parent card. Both plan and workflow decisions use their existing authorized
decision endpoints with the same idempotency keys as the inbox. Workflow configuration
can be reviewed on the Approvals page. Approval failures appear inline on the card.
Errors keep the request actionable
and retries reuse their decision key. Only successful decisions dismiss the row and
refresh the overview. `PendingAgentQuestions` remembers completed approval IDs within the
current organization so stale reads cannot restore decided rows or inflate its count.

Hiring suggestion cards read the company's pending recommendation backlog,
including recommendations never materialized as chat actions. `CompanyDashboardEndpoints`
maps `GET /api/core/organizations/{organizationId}/dashboard/hiring` to
`IHiringService.ListRecommendationsAsync` and returns `HiringBacklogResponse` only to
active managers and owners in that organization. Suggestions with remaining headcount
are ordered by priority, then creation date, and link to their existing `HiringUrl`.
The queue previews five approvals and five recommendations with a shared expansion
control. Approval, recommendation, and question loading/errors remain independent.

`OverviewHiringCandidateCard` shows the selected candidate from `SelectedCatalogAgentJson`,
including portrait, publisher, listed cost (or **Cost unavailable**), and
`SelectionRationale` inside one tinted panel. Candidate identity and rationale sit in two
columns with a vertical divider, stacking with a horizontal divider on narrow screens.
The right-aligned **Hire** action opens the shared `AgentHireDialog` with the selected
candidate, role title, and recommendation ID. **Browse Candidates** uses `HiringUrl`,
retaining the approved plan context. Missing or malformed candidate data leaves browsing
available without fabricating a candidate or cost. While both candidate data and selection
rationale are empty, `OverviewHiringCandidateCard.IsInterviewing` shows **Interviewing agents**
with an indeterminate progress indicator in the same tinted candidate panel. Realtime refreshes
replace that panel's contents with the selected agent when available. A completed selection
without a candidate (manual choice, human-required coverage, or no eligible match) shows
**Candidate review** with the persisted rationale, without a progress indicator. Malformed or
incomplete candidate data shows **Recommendation unavailable** and retains contextual browsing.
The preparation panel is a polite live status region. The dialog's `Hired` callback refreshes
the dashboard backlog. The header attributes the suggestion to `SuggestedBy` (the recommending
employee), using that employee's portrait. When `RequestingEmployee` differs, the header
shows the recommender's installed role and a secondary **Role requested by** line names the
plan requester and `RequestingTeam`. Otherwise the team remains in the header context.
Recommendations without a known recommender fall back to the requester, then a generic hiring
label; same-person requests do not repeat attribution. `EmployeePortraits.FindEmployee`
resolves recommender portraits only for an unambiguous employee name in the current organization.

`DashboardWidgets` groups Hiring, Approvals, and Decisions in the **Needs your
attention** widget without tabs. Legacy layouts merge these queues at the first saved queue position.
`CSweetDbContext.CaptureHiringRecommendationEvents` captures `WorkforcePlan` changes
into the same save's application realtime outbox as
`AppRealtimeEvents.HiringRecommendationsChanged`. `CommandCenter` re-reads current
state on those events, approval changes, and connection/reconnection, and queues a
follow-up when a relevant event arrives during loading. Approvals and recommendations
remain independent lifecycles; dashboard navigation does not decide or complete either.

## What a suggestion is

`Marketplace.SearchAsync` passes its existing recommendation ID to the organization-scoped
available-agents endpoint. `HiringService.GetCandidateSearchContextAsync` reads the recommendation
within that organization and uses its approved desired role's category and preferred specializations,
falling back to the recommendation role key. `AgentCatalogService` applies `RoleTaxonomy.SatisfiesRole`
to include every agent in that base family; specializations affect ranking only. The role title remains
the hire's display context. Missing or inaccessible recommendations return an error.

Legacy role-only links resolve exact catalog role keys, names, aliases, or declared categories when
they identify one unambiguous core family; other values retain text matching. Marketplace discovery
receives explicit search text rather than the specialized role label, so upstream text filtering cannot
hide generalists. Existing recommendation URLs and hire-time role validation remain compatible.

- `SuggestedUserAction` (`src/CSweet.Domain/Core/ConversationMessage.cs`) — one actionable workflow for
  a specific conversation. For hiring it stores `{ "role", "recommendationId" }` in `ParametersJson`.
- A suggestion is created by a granted agent capability and, once attached to a message or completed
  turn, is materialized into a `ConversationMessage` with `SourceProvider = "SystemAction"`
  (`SuggestedUserActionMaterializer` in `src/CSweet.Infrastructure/Communications/UserActionService.cs`).
  The Communications page renders it as `HiringSuggestionCarousel` when every action on the message is
  `hiring.marketplace.browse.v1`; consecutive SystemAction messages with the same `CausationId` merge
  into one carousel (`CommunicationHubService.IsGroupedHiringActionMessage`).

## Status state machine

| Status | Meaning | Set by |
| --- | --- | --- |
| `Pending` | Actionable; "Browse candidates" button shown | Creation |
| `Completed` | Role fulfilled by an actual hire | `HiringService.CompleteSuggestedHiringActionsAsync` |
| `Cancelled` | Recommendation withdrawn without a replacement, or the owning turn was cancelled before materialization | `HiringService.WithdrawRecommendationAsync` cascade; `ChatTurnService.CancelPendingActionsAsync` |
| `Superseded` | A newer suggestion replaced this one; `SupersededAt`, `SupersededByActionId`, `SupersededByRole` carry lineage | `UserActionService.SupersedeEarlierSuggestionsAsync` |

Terminal states are non-actionable: the carousel renders them muted with no button, and the generic
suggested-action card stops offering its button.

`UserActionService.SuggestAsync` shares `HiringOrganizationLock.AcquireAsync` with recommendation
mutations and hiring confirmation. The transaction lock covers the current recommendation read,
dedupe, supersession, materialization and save. A delayed suggestion for a fulfilled recommendation
reuses an existing completed card or creates a completed card linked to the recorded final hire;
it cannot supersede another role. Withdrawn recommendations produce non-actionable cancelled cards.
Explicit recommendation IDs must resolve within the current organization; legacy role-only actions
retain their existing behavior. Partial multi-seat fulfillment remains actionable.

`HiringService.CompleteSuggestedHiringActionsAsync` completes both pending and superseded cards for
the fulfilled recommendation, clears obsolete replacement lineage, and uses
`SuggestedUserActionMaterializer.CompleteAsync` to queue participant-scoped completion events in
the same save. Cancelled actions remain cancelled. `ChatTurnService.CompleteAsync` materializes
turn-bound completed actions too, so hiring before the assistant finishes cannot hide the card.
Migration `RepairFulfilledHiringSuggestions` repairs pending/superseded historical cards only when
the same organization's approved recommendation has full headcount and a recorded hire. Its
state update and completion outbox insert are atomic and idempotent.

Regression coverage: `UserActionServiceTests` in `HiringSuggestionLifecycleTests` exercises late
suggestion creation, superseded completion, replay, multi-seat fulfillment, organization scope,
withdrawal and turn-bound completion. `HiringSuggestionPostgresTests` forces both concurrent
operation orders using the shared PostgreSQL lock and verifies the repair SQL and event recipients.
Set `CSWEET_HIRING_TEST_POSTGRES` to a loopback test server to run these isolated database tests.

Rules:

- One actionable suggestion per conversation. `UserActionService.SuggestAsync` returns the existing
  `Pending` action when the same conversation + recommendation is requested again, and marks earlier
  `Pending`/`Cancelled` suggestions in that conversation `Superseded` with lineage.
- Suggestions created by the same source (the request's `MessageId` or `ChatTurnId`) are one
  multi-role batch and are never superseded by each other. Materialization gives every action its own
  SystemAction message and clears `ChatTurnId`, so `SupersedeEarlierSuggestionsAsync` identifies the batch
  by the materialized message's `CausationId` (the source id), the same key that groups the carousel.
  Migration `RestoreSameBatchHiringSuggestions` returns roles that were wrongly superseded by a sibling in
  the same batch to `Pending` when their recommendation is still pending.
- Withdrawing a hiring recommendation cancels its `Pending` suggestions even when no replacement
  exists, so a stale "Browse candidates" button cannot survive into a dead Marketplace route.

## Creation paths

- Immediate: `SuggestUserActionRequest.MessageId` materializes the widget in the same save.
- Turn-attached: `SuggestUserActionRequest.ChatTurnId` materializes when the turn completes
  (`ChatTurnService.CompleteAsync`). If the turn fails or is cancelled first, the pending action
  becomes `Cancelled` (`CancelPendingActionsAsync`).

The Chief of Staff agent never calls `suggest_user_action` from model responses; its runtime attaches
the action for new or changed top recommendations (`ChiefOfStaffAgent.AttachMentionedHiringActionAsync`
in the `CSweet.Agent.ChiefOfStaff` repository).

## Realtime events

Suggestion state changes are written to `ApplicationRealtimeOutbox` and published to conversation
participants. Communications refreshes on any `com.csweet.communication.*` event, so new event types
need no client plumbing.

- `com.csweet.communication.user-action.created.v1`
- `com.csweet.communication.user-action.completed.v1`
- `com.csweet.communication.user-action.superseded.v1`
- `com.csweet.communication.user-action.cancelled.v1`

## Code map

| Concern | Location |
| --- | --- |
| Entity + fields | `src/CSweet.Domain/Core/ConversationMessage.cs` (`SuggestedUserAction`) |
| Status/event constants + response DTO | `src/CSweet.Contracts/Communications/CommunicationHubContracts.cs` |
| Create / dedupe / supersede + materializer + events | `src/CSweet.Infrastructure/Communications/UserActionService.cs` |
| Withdraw cascade + completion | `src/CSweet.Infrastructure/Core/HiringService.cs` |
| Message projection + grouping | `src/CSweet.Infrastructure/Communications/CommunicationHubService.cs` |
| Turn-attached materialization + cancel | `src/CSweet.Infrastructure/Core/ChatTurnService.cs` |
| MCP tool (`suggest_user_action`) | `src/CSweet.AgentHost/Broker/McpToolCatalog.cs` + `CommunicationHubCapabilityHandler.cs` |
| Carousel UI | `src/CSweet.UI/Components/Hiring/HiringSuggestionCarousel.razor` (+ `.razor.css`) |
| Host + generic card | `src/CSweet.UI/Pages/Communications.razor` (+ `.razor.css`) |

## Owner-directed replacement

When the owner replaces a suggested role ("actually just hire a software developer"), the Chief
withdraws the old recommendation and upserts the replacement. The platform cancels the old widget and
the new suggestion supersedes it with "Cancelled — replaced by <role>" lineage, while the replacement
gets its own widget. Covered by `UserActionServiceTests`, `HiringServiceTests`
(`WithdrawRecommendation_CancelsPendingMarketplaceSuggestion`) and `CommunicationsLayoutTests`.

## Repeat hires from Marketplace

`HiringService.PreviewMarketplaceHireAsync` treats **Review and Hire** as a new employee
request. When the catalog selects an installed agent instance that already belongs to an
employee, `BuildWorkflowSnapshotAsync` pins its approved package and grants, then
`ConfirmWorkflowCoreAsync` creates a new employee from the same agent definition. For
older installations without a definition, confirmation first reuses the approved package
to create one. Each employee receives a separate business-scoped agent instance and
configuration; the package is not fetched or installed again. The package manifest must
declare `supportsMultipleInstallations`.

## Configuration isolation during hiring

`HiringService.ConfirmWorkflowCoreAsync` requests
`InstallAgentRequest.ReuseExistingDefinition` when importing a catalog hire.
`AgentDefinitionService.ImportAsync` reuses the existing definition for the same
package without changing its configuration, grants, schedule, or revision. A different
package requires an explicit definition update; hiring does not silently upgrade other employees.
The first import still initializes the new definition's defaults.

Submitted employee settings are persisted in `EmbeddedAgentInstallSnapshot.ConfigurationSettings`
so `HiringService.ProcessNextAsync` retains them after a build wait.
`OrganizationUserService.CreateAsync` validates `CreateOrganizationUserRequest.ConfigurationOverrides`
against the approved manifest and provider catalog, then saves them on the new
`AgentInstallationConfiguration` alongside the employee before runtime activation.
Explicit values are retained even when equal to current shared defaults.

`AgentDefinitionLifecycleTests.HiringWithCustomProvider_DoesNotChangeAnotherCompanyOrSharedDefaults`
covers two companies, inherited and explicit existing settings, and invalid-provider rejection.
`HiringServiceTests.EmbeddedAgentWorkflow_PreviewsPinsInstallsAndHiresConfiguredRepository`
covers configuration retention through deferred build completion.


`BusinessOnboardingService.ProcessNextAsync` uses the same definition reuse rule.
`CreateChiefAssignmentAsync` applies the persisted onboarding settings through
`OrganizationUserService.ConfigureHiredInstallationAsync` before saving the Chief's
installation. `BusinessOnboardingServiceTests.DurableOperation_ContinuesAfterHandoffAndCreatesOneBusiness`
also verifies that onboarding another company preserves the first Chief's settings.

## Approvals inbox and manager review

Hiring suggestions and team-design approvals have separate lifecycles.
`ResourceChangeService.DecideAsync` records the assigned manager's decision on
`ResourceChangeRequestRecord`; completing a hire does not decide that request.

`ApprovalDashboardService.GetAsync` excludes pending requests the signed-in user cannot
decide, including from **All activity**. Its `PendingCount` and the sidebar's
`ApprovalState.PendingCount` count only actionable pending items. Completed decisions
remain available in **All activity**. `ManagedActionApprovalAuthority` shares the
manager-versus-owner policy between the dashboard and `ApprovalEndpoints`: owner status
does not override Manager Approval. Missing configuration defaults to Manager Approval;
inactive requesters do not supply manager authority. Connector requests retain their
exact assigned-approver binding and expiry checks.

`ChiefOfStaffAgent.CommitResponseWithHiringActionAsync` persists candidate selection and the turn-bound suggestion before `AgentTurnStreamWriter.CommitAsync`. A final commit ends the interactive turn and may stop the work attempt; required card creation must not run after it. `HiringSuggestionCarousel` renders the candidate portrait, publisher, cost, selection rationale, and plan proposer, and opens the same `AgentHireDialog` used by Marketplace.
