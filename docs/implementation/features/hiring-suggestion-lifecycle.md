# Hiring suggestion (suggested action) lifecycle

This is the authoritative reference for the "HIRING PLAN / Hiring suggestions" widget that appears in
Communications when an agent attaches a Marketplace action to a message or chat turn, and for the
company overview's hiring row.

## Hiring autonomy and candidate review

[Chief hiring autonomy](chief-hiring-autonomy.md) documents the authoritative owner policy,
approval-time delegation, candidate selection, and automatic execution boundary.
`WorkforcePlan.SelectedCatalogAgentJson` and `SelectionRationale` populate recommendation
cards. `HiringSuggestionCarousel` and `Marketplace` both render `AgentHireDialog`, sharing
preview, configuration, grants, cancellation and confirmation logic. A selected candidate
shows **Hire agent** and **See other candidates**; otherwise the action remains **Browse
candidates**. Existing lifecycle states continue to govern action availability.

Marketplace context uses authorized server attribution and remains attached when search
filters are cleared. **Browse outside this plan** explicitly detaches it. Unavailable or
fulfilled recommendations disable contextual hiring. Automatic progress is persisted as
`DelegatedHiring` conversation messages alongside hire-operation outbox events.

## Company overview hiring row

`CommandCenter` renders the reorderable **Hiring** row through `CompanyHiringSummary`.
Its **Approvals awaiting you** group contains pending, decidable `ResourceChange` and
`HiringWorkflow` items from `ApprovalDashboardService`. Those items are excluded from
the overview's **CEO approvals** preview and count, while the full inbox and sidebar
retain their existing counts and authority rules. Review links supply `approvalId` to
`Approvals`, which puts the actionable selected request first and highlights it. A
completed, unavailable, or non-decidable request produces a notice without expanding access.

The **Hiring suggestions** group reads the company's pending recommendation backlog,
including recommendations never materialized as chat actions. `CompanyDashboardEndpoints`
maps `GET /api/core/organizations/{organizationId}/dashboard/hiring` to
`IHiringService.ListRecommendationsAsync` and returns `HiringBacklogResponse` only to
active managers and owners in that organization. Suggestions with remaining headcount
are ordered by priority, then creation date, and link to their existing `HiringUrl`.
Both groups preview five items with independent expansion, loading, and error states.

`DashboardWidgets` places Hiring after Pending Decisions for new/default layouts;
existing four-, five-, and six-widget layouts retain their order and append Hiring.
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
