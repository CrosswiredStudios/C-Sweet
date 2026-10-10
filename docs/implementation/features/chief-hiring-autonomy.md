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
packages' requested grants for approved roles, subject to the existing platform restrictions.
Unknown cost, missing required configuration, or insufficient authority requires owner review.
Publisher preference is independent: first-party only, prefer first-party, or best fit.
Catalog provenance is platform-controlled. `AgentCatalogService.DeduplicationKey` keeps
identical manifest IDs from different repositories separate; publisher names do not confer trust.

`CSweetDbContext.CaptureHiringPlanDelegations` snapshots completed policy when a resource
change becomes approved, in the same save as approval state and its notification outbox.
The approval's authorized manager decides the roles; the owner's delegation decides whether
the Chief may execute hiring. `BoundPolicyAsync` provides conservative recovery for old plans.
`ApplyToPlanAsync` is an explicit owner action to apply saved policy to an existing approved
plan. The policy captured at approval and current policy must both permit each uncommitted
hire. Policy changes cannot expand an existing plan implicitly.

## Conversation and owner settings

`ChiefOfStaffAgent.HiringPolicy.cs` asks one existing executive-decision widget at a time,
after Chief profile setup and before company focus. The sequence is mode, automatic level
if relevant, bounded costs/permissions if relevant, then publisher preference. Explicit
publisher preferences supplied with an answer carry forward without another question.
An ambiguous automatic request prompts for the level; incomplete setup executes in
recommendation mode. An explicit skip completes setup with the defaults and resumes focus.
Decision keys bind installation, stage, revision, and retry; replayed answers do not increment
policy revision again.

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

PostgreSQL transaction-scoped organization advisory locks serialize policy changes, withdrawals,
and hire commits, including aggregate spending and headcount consumption. State and outbox
writes commit together. Background operations revalidate current authority; recovery does not
convert a delegated workflow into implicit owner confirmation. Already completed slots return
success without another hire. Existing resource-change/recommendation reads and durable events
support bounded reconnect discovery. Successful asynchronous hires continue remaining slots.
`QueueOperationChanged` persists automatic hiring progress/completion in the owner conversation.

## Shared hiring UI and marketplace context

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
