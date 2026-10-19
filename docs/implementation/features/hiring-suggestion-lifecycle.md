# Hiring suggestion (suggested action) lifecycle

This is the authoritative reference for the "HIRING PLAN / Hiring suggestions" widget that appears in
Communications when an agent attaches a Marketplace action to a message or chat turn, and for the
company overview's hiring row.

## Employee dismissal

`OrganizationUserService.DeleteAsync` calls `EmployeeActionExpiration.ExpireAsync` before
detaching the installation. Dismissal and expiration share the organization's hiring lock
and one transaction, including the durable realtime outbox records. Pending recommendations,
linked owner previews and delegated hiring workflows, unfinished hire operations, suggested
actions, executive questions, managed actions, resource-change and replenishment requests,
document-access requests, source-control approvals and their waiting operations, and prepared
connector actions become **Cancelled**. Passive Chief audit cards also settle as cancelled.
Completed hires, answers, artifacts and historical records remain available.

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
