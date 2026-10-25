# Chief of Staff hiring autonomy

## Policy and authority

`ChiefHiringPolicy`, `ChiefHiringPolicyRevision`, and `HiringPlanDelegation` in
`CSweet.Domain/Core/HiringPolicy.cs` persist owner authority per organization and Chief
installation. `HiringService.Autonomy.cs` implements `IHiringAutonomyService`; retrieved
memory and model-generated text never authorize execution.

The default is **Recommend candidates**, **Prefer first-party**, with setup incomplete.
No policy row is necessary to obtain this safe default. Existing installations therefore
receive recommendations immediately and are offered setup during a later hiring interaction;
installing a replacement starts fresh, while upgrading the same installation preserves policy.

Modes are **Choose candidates yourself**, **Recommend candidates**, and **Automatic hiring**.
Automatic levels are **Approved packages**, **Within limits**, and **Broad delegation**.
Within limits requires explicit per-hire and total costs, currency, capability keys, and
network entries. Empty permission lists authorize none. Broad delegation allows eligible
packages' requested grants for the Chief's own leadership recommendations and approved
team roles, subject to the existing platform restrictions.
Unknown cost, missing required configuration, or insufficient authority requires owner review.
`HiringService.ConfigureDelegatedHireAsync` resolves non-secret manifest defaults and saved
definition settings into the hire snapshot before confirmation. When both live and bound
authority allow broad delegation, a package with no provider/model defaults can reuse the
Chief's effective configured provider/model pair. It never copies unrelated settings or
credentials. `ValidateDelegatedConfigurationAsync` checks required visible fields, types,
dependencies, budgets and enabled providers again at deferred commit; real missing input
names the fields instead of requiring a redundant hiring approval. Frozen settings do not
follow later shared-default edits while a package builds.
Publisher preference is independent: first-party only, prefer first-party, or best fit.
Catalog provenance is platform-controlled. `AgentCatalogService.DeduplicationKey` keeps
identical manifest IDs from different repositories separate; publisher names do not confer trust.

`CSweetDbContext.CaptureHiringPlanDelegations` snapshots completed policy when a resource
change becomes approved, in the same save as approval state and its notification outbox.
The approval's authorized manager decides the roles; the owner's delegation decides whether
the Chief may execute hiring. `BoundPolicyAsync` provides conservative recovery for old plans.
`ApplyToPlanAsync` is an explicit owner action to apply saved policy to an existing approved
plan. The policy captured at approval and current policy must both permit each uncommitted
hire. Policy changes cannot expand an existing approved team plan implicitly.

Standalone company leadership recommendations (no `SourceResourceChangeRequestId`,
`TeamId`, or `WorkstreamId`) use the completed current owner policy in `BoundPolicyAsync`.
They do not require a separate resource-change approval or a `HiringPlanDelegation` row.
This includes outstanding leadership suggestions when the owner enables automatic hiring.
`SubmitDelegatedCoreAsync` supplies the policy owner as reporting manager;
`ValidateDelegationAsync` rechecks that manager and the recommendation's role, objective,
team and workstream before deferred commits. Team/workstream staffing still requires its
approved resource-change plan. Publisher provenance, costs, grants, configuration, headcount,
revocation, and duplicate-hire checks apply to both paths.

## Conversation and owner settings

`ChatTurnWorker` sends the bound `CurrentMessageContent` alongside the complete model
prompt. `MemoryRecallDispatchEvidence.ValidatePromptPayload` verifies its hash against
the frozen current-message source. The Chief's `ReadCurrentUserMessage` uses that field
for deterministic hiring setup, avoiding preference changes inferred from quoted history
or retrieved memory. Older platforms use the final current-message prompt wrapper.

Hiring setup answers finish with a `terminal-question-message` progress record naming
the saved next-question message. `ChatTurnWorker.ResolveTerminalQuestionMessageAsync`
validates its conversation, Chief sender, requesting installation, creation time and
turn association, then reuses it as the turn response without appending an acknowledgement.

`ExecutiveBriefingService.DeferForOnboardingAsync` prevents activation, runtime-startup,
and scheduled briefings for a newly onboarded Chief until lifecycle delivery and owner
hiring setup are complete. `ManagementReviewScheduler` applies the same check when
dispatching queued automatic briefings. Explicit manual requests remain available.

`ChiefOfStaffAgent.HiringPolicy.cs` asks one existing executive-decision widget at a time,
after Chief profile setup and before company focus. The sequence is mode, automatic level
if relevant, bounded costs/permissions if relevant, then publisher preference. Explicit
publisher preferences supplied with an answer carry forward without another question.
An ambiguous automatic request prompts for the level; incomplete setup executes in
recommendation mode. An explicit skip completes setup with the defaults and resumes focus.
Decision keys bind installation, stage, revision, and retry; replayed answers do not increment
policy revision again.

Chief 2.13.5 reads the live policy into model instructions before each assistant generation.
Automatic delegation does not require a Marketplace readiness or selected-candidate
confirmation question. `HiringTurnTools` guards native tool persistence: one response may
change staffing or ask one essential question, and cannot do both in either call order.
`PrepareHiringCandidateAsync` remains responsible for executing the platform-authorized
hire and retaining shared review only for an actual exception. Install the published agent
update through Installed Agents; a local agent build does not update installed payloads.

`ChiefOfStaffAgent.HandleConfigurationChoiceAnsweredAsync` resumes hiring setup before company
focus, matching the initial onboarding path. `BeginFocusOnboardingAsync` shares conversation-scoped
message and decision keys across all entry paths and persists delivery in
`chief.onboarding-focus:<conversationId>` operating state. The state is written after the required
focus card succeeds; retries after partial delivery reuse the durable message/card. It also retains
the originating answer turn so later hiring-preference changes acknowledge the save without
repeating the introduction or attaching an earlier question to a new turn. Chief 2.12.1 contains
this fix; upgrade the installed agent package to apply it. Existing conversation history is preserved.

`CaptureDecisionAsync` accepts only an immutable, answered decision for the authenticated
Chief, answered by a current human owner. It supports selected options and explicit natural
language, including bounded costs such as `10 USD per hire, 100 USD total`. Capabilities are
hidden from general model tool selection; the Chief's deterministic runtime captures decisions.
The decision ID/answer turn alone does not confer authority without these server validations.

`QueuePolicyMemoryAsync` stores the saved preferences and rationale in the Chief's scoped owner
conversation and `MemoryCaptureOutbox`. This is a memory mirror, not an execution grant.
`ChiefHiringSettings.razor` exposes an owner-editable view through `HiringEndpoints` policy
GET/PUT and explicit existing-plan apply routes. Optimistic revisions reject stale updates.

## Candidate selection and execution

`FirstPartyAgentCatalogProvider.Map` and `MarketplaceDiscoveryClient.FirstPartyAgents`
report all current first-party agent packages as free (`0 USD`, rather than an unknown
price). `AgentCatalogService.Consolidate` and `ResolveAsync` carry that price onto installed
copies only when the package repository and manifest identity match the first-party listing.
Unknown prices from other sources continue to require owner review. Package price does
not include inference-provider or compute charges.

`SelectCandidateAsync` uses approved role requirements, canonical role-family eligibility,
availability, specialization ranking, and publisher preference. It persists the selected
`AvailableAgent` and concise rationale on `WorkforcePlan`. Choosing candidates manually
clears a prior selection. Human-required roles and unavailable candidates retain an actionable
explanation rather than inventing a package.

`SubmitDelegatedAsync` binds every request to the authenticated active Chief and its live
recommendation. Each headcount slot uses `delegated-hire:{recommendation}:{fulfilledCount}`
with the existing immutable marketplace preview and `AgentHireOperation` pipeline.
`StaffingActionProposal.DelegatedInstallationId` marks the explicit delegated path.
`ValidateDelegationAsync` checks both policies, package provenance, prior approved definition
or explicit grants, currency/cost and remaining total allowance, and required configuration.
`ConfirmWorkflowCoreAsync` also revalidates package digest, grants, approved role/headcount,
team, manager, and the existing platform hire restrictions before creating the employee.
Manual confirmation continues to require an owner and replaces delegated authorization.

`ConfigureDelegatedHireAsync` retains valid candidate defaults and freezes validated settings
in the hire snapshot. Under broad delegation, a missing/empty model pair or a deleted/disabled
default provider falls back to the explicit system default provider and its `DefaultChatModel`.
`DefaultLlmSelection.ReadAsync` is shared with new-installation defaults. Older installations
without a usable system default may reuse the Chief's current validated pair. Only the pair is
copied; secrets and unrelated settings never transfer. Narrower policies still require review,
and a previously frozen snapshot is never silently rewritten.

`LlmProviderProfileService` serializes default selection, deletion and disabling. Deleting or
disabling the default requires explicit selection of another enabled, tested provider/model
when any other profiles remain, including disabled ones. The last provider may be removed;
default references are then cleared. Settings exposes the pair and a replacement picker in
the delete flow. `default_provider_replacement_required` returns HTTP 409 before secret deletion.

`WithHiringModesAsync` exposes `AutomaticHiringBlockReason` from the source-linked blocked
hire attempt. Overview and Communications show **Automatic hiring needs attention** with a
shared **Review hiring** action. Saved automatic mode does not imply that a hire is progressing.

PostgreSQL transaction-scoped organization advisory locks serialize policy changes, withdrawals,
and hire commits, including aggregate spending and headcount consumption. State and outbox
writes commit together. Background operations revalidate current authority; recovery does not
convert a delegated workflow into implicit owner confirmation. Already completed slots return
success without another hire. Existing resource-change/recommendation reads and durable events
support bounded reconnect discovery. Successful asynchronous hires continue remaining slots.
`QueueOperationChanged` persists automatic hiring progress/completion in the owner conversation.
Chief `RecoverApprovedHiringAsync` also discovers standalone unfilled suggestions on activation.
`PrepareHiringCandidateAsync` suppresses manual marketplace actions for successful, starting,
queued, building and completing hires; review/configuration failures retain the shared review.
Deploy the platform update and Chief 2.13.3 through the normal published release and Installed
Agents update flow. Local validation does not change installed packages.

Chief 2.13.4 moves activation recovery to durable attention callbacks. `ChiefWorkCards` binds
the callback's personal card before dispatch; `SubmitDelegatedAsync` also creates a dedicated
automatic-hiring card before candidate selection. `WorkflowSnapshot.AuditWorkItemId` carries
that card through asynchronous builds. Operation transitions append card activity and persist
board refresh hints with their state changes. Tracking cards are non-executable and do not wake
another hiring callback. See [Chief work cards](employee-audit-timeline.md#chief-of-staff-work-cards).

## Shared hiring UI and marketplace context

`HiringRecommendationResponse.AutomaticHiringEnabled` reflects both the live owner policy
and the role's approved-plan delegation. Automatic cards show **Automatic hiring** and
`AutomaticHiringControl` instead of **Hire** or **Browse Candidates**. A no-suitable-agent
exception retains its Marketplace fallback.

**Choose this role manually** POSTs to `HiringEndpoints`' recommendation-scoped
`choose-manually` route and updates the card in place; it never navigates. After the
server confirms cancellation, **Browse Candidates** is available, so owners can stop
several roles without leaving the overview or Communications. Errors leave the automatic
card visible. A hire that already committed cannot be undone by this button.

`HiringService.ChooseRoleManuallyAsync` requires a current owner, stores the requesting
owner and timestamp on `WorkforcePlan`, and cancels only that role's unfinished delegated
workflows and hire operations. It settles the automatic-hiring audit card, preserves the
unfilled recommendation and completed seats, and commits realtime outbox hints with the
change. The organization's hiring lock serializes the switch with candidate selection,
hire commits and deferred recovery. `SubmitDelegatedAsync`, `BoundPolicyAsync` and
`ValidateDelegationAsync` enforce the saved override after reconnect and policy edits;
an explicit manual Marketplace hire remains possible. Other roles and the Chief policy
are unchanged. `RoleScopedManualHiring` adds the two nullable tracking columns.

`EmployeeWorkspaceHeader` exposes Agent Marketplace beside Org chart, Teams, Directory and
Hiring. `Marketplace` owns `/organizations/{OrganizationId}/employees/marketplace` and retains
`/organizations/{OrganizationId}/marketplace` as an alias for existing hiring/onboarding links.
Both routes render the Employees workspace and bind the same `role` and `recommendationId`
query parameters. The active marketplace tab preserves the query, so reselecting it retains
the recommendation's team and role. `BusinessNavigation` highlights Employees for both routes;
switching businesses keeps the marketplace tab but drops the previous business's context.

`Components/Hiring/AgentHireDialog.razor` owns marketplace preview, configuration controls,
grant review, confirmation, cancellation, and operation status. Both `Marketplace.razor` and
`HiringSuggestionCarousel.razor` render this exact component. Candidate cards show identity,
publisher, cost if known, role-fit explanation and plan proposer. **Hire agent** opens shared
review; **See other candidates** keeps the recommendation in the marketplace URL.
Permission/configuration exceptions use the same selected candidate and review flow.

Marketplace loads attribution from the authorized recommendation endpoint. The persistent
banner shows role, original proposer, team, remaining headcount, approver, and a plan-review
link. Searching and clearing filters preserve recommendation binding. **Browse outside this
plan** explicitly detaches it. Missing, inaccessible, withdrawn or fulfilled recommendations
disable contextual hiring and show a notice. Existing cancellation, supersession, completion,
and multi-role carousel behavior remains in the suggested-action lifecycle.

## Migration and validation

`20261010061307_ChiefHiringAutonomy` adds policy/history/delegation tables, candidate selection
columns, and delegated workflow attribution. Apply it through the normal deployment migration
process; it does not give old Chiefs or old plans automatic authority.

Regression coverage is in `HiringServiceAutonomyTests`, `AgentCatalogServiceTests`,
`HiringCandidateEndpointTests`, `CommunicationsLayoutTests`, and Chief
`OperatingProfileOnboardingTests`. SDK `HiringAutonomyContractTests` verifies default wire
behavior. `HiringAutonomyPostgresTests` creates and deletes a unique loopback PostgreSQL
database, verifying duplicate slots and competing spending commits with separate contexts.
Set `CSWEET_HIRING_TEST_POSTGRES` to run it; never supply a production connection. Package
and employee services are test doubles; real package execution still requires environment QA.
