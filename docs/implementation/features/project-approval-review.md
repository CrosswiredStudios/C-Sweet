# Project approval review

`Approvals` presents one Awaiting you / History navigation level. Pending agent questions are shown beneath the approval inbox. `ProjectCreationApprovalCard` renders project purpose, rationale, accountable lead, budget, target, approval effects, delegated actions and required human decisions. Milestones, success criteria, staffing, authority limits and submitted document revision numbers are expandable; hashes and routing keys live under Technical details. Missing dates and budget remain explicitly unspecified.

## Source and authority

- `ProjectApprovalReader` projects `workstream.create.v2` from the stored `ActionProposal.PayloadJson.payload`, resolving people and documents within the proposal organization. It does not accept agent-authored HTML or UI fields. Malformed plans produce a review error with no decision controls.
- `ApprovalDashboardService` uses this projection for both pending and resolved project proposals. The authorized `GET /api/core/organizations/{organizationId}/approvals/projects/{proposalId}` supports exact lookup, including proposals beyond the bounded dashboard history.
- `ApprovalEndpoints` retains the existing `DecideManagedAgentActionRequest` binding (hash, revision, resource and idempotency key). A PostgreSQL transaction and proposal advisory lock serialize competing human decisions before checking status. `WorkstreamManagedActionExecutor` performs creation before approval succeeds. Failure remains pending and does not report a created project.
- Project decision, `project-approval-decision` receipt in `PluginOperationalStates`, completed suggested-action state, and approval realtime outbox entries share the transaction. The receipt preserves feedback, decision kind, decision maker and created project. Existing historical proposals without receipts can still resolve their created workstream through `SourceProposalId`.

## Inline tool workflow

`suggest_user_action` (`platform.user-action.suggest.v1`) accepts `workflowType: approval.review.v1`, `parameters: { approvalId }`, and either the agent's message ID or chat turn ID. This first workflow supports project-creation proposals. `ApprovalUserActionWorkflowResolver` verifies the proposal's organization, originating installation and action type, and generates the exact approvals deep link. `UserActionService` requires a private two-participant conversation containing the requester and assigned approver and deduplicates repeated presentation of the same proposal. Existing turn materialization/cancellation behavior applies.

`CommunicationHubService.ToAction` returns the platform-resolved approval ID. `InlineApprovalCard` reads current authorized state and renders `ProjectCreationApprovalCard` in compact mode with Approve / More info / Deny. It never uses the message text or a stored Pending badge as an execution grant. Read-only perspectives suppress decisions. Approval realtime events and reconnect trigger fresh reads; the POST endpoint independently revalidates authority and binding. Decisions made from either surface therefore converge on the same state.

The Creative Director agent's `PresentProjectApprovalAsync` attaches this workflow after saving the project proposal. It retries presentation with stable keys while awaiting the project, so a process interruption between proposal creation and attachment is recoverable. Version 1.17.0 declares the additional suggestion capability; deployed installations must review it on upgrade.

## Verification

`ProjectApprovalReviewTests` covers typed projection, missing values, malformed plans, organization/approver isolation, workflow ownership and duplicate presentation. `ProjectApprovalEndpointTests` exercises the HTTP decision/read pair, binding rejection, preserved feedback and stale decisions. `ProjectApprovalRenderingTests` covers escaped proposal text, full/compact layouts, exact navigation and read-only controls. Agent `ProjectApprovalCardTests` checks stable message/proposal binding on recovery.

## Reporting-manager project review

`ProjectApprovalGovernance` provides `platform.project-approval.read.v1` and
`platform.project-approval.decide.v1` through `PluginOperationsCapabilityHandler` and
`McpToolCatalog`. Reads return the stored command binding, current assigned approver,
spending policy and decision receipt. Pending discovery is capped at 100 relevant
proposals; exact-ID reads support durable recovery. Submitters can read their own
resolved proposals. Agents re-read current state rather than trusting event payloads.

The reporting manager can approve, request revision, reject or escalate. Escalation
preserves the exact proposal and payload hash, stores the rationale and decision actor,
and routes it to the reviewer's active reporting manager. The dashboard, exact project
reader and human decision endpoint use that route. Manager approval cannot be bypassed
by owner status. `Withdraw` permits only the submitting agent to withdraw its own
pending proposal, including replacement of older Creative Director-authored requests.

Approval uses the registered `IManagedActionExecutor` before recording success. A
PostgreSQL proposal lock and transaction cover execution, decision receipts, completed
suggested actions and notification outboxes. Exact command receipts make retries
idempotent and reject reuse of a decision key for different content. The legacy
managed-action agent decision capability delegates project decisions to this path too.

Spending is explicitly `Unlimited` by default, including when no budget amount is
specified, because the system does not currently execute real-money spending.
`maximumProjectBudget` (zero/unset means unlimited) and `projectBudgetCurrency` provide
an optional delegated limit for manager review. A positive limit requires a proposed
amount within the limit and a matching currency; agents must request revision or
escalate unverifiable or excessive budgets. Future finance integration should replace
this policy source with authoritative budget/current-commitment reads at both review
and execution. Unlimited spending does not delegate legal commitments, material
strategy changes, publication or launch.

`VideoGameCreativeDirectorAgent.EnsureProjectFoundationAsync` initiates a durable
coordination session carrying the full project template and exact accepted document
references. `SpecialistAgent.ProposeProjectFoundationAsync` analyzes and submits it as
Producer. `ReviewProjectFoundationAsync` reads the authoritative proposal and the
Creative Director reviews scope, success criteria, ownership, milestones and authority
against accepted evidence. Request-revision feedback returns through the session and
produces a new approval-bound proposal. Only an authoritative created project permits
production setup to advance. Pending legacy Director proposals are withdrawn and
replaced after upgrade; approved projects are retained.

`ReviewAssignedProjectsAsync` handles durable review wakes and attention/reconnect
recovery. Producer manager-chat proposals retain the path without a Creative Director;
`RememberSubmittedProjectAsync` and `RecoverSubmittedProjectsAsync` preserve a bounded
proposal index and recover revision feedback after missed events. Model plans and
review assessments are cached before their bound mutations. New grants/subscriptions
must be reviewed when upgrading Creative Director 1.18.0 and Producer 2.19.0.

Verification: `ProjectApprovalGovernanceTests`, platform project approval endpoint/read
and rendering tests, Creative Director `ProjectApprovalReviewTests` and Producer
`ProjectFoundationTests`, plus both agents' full test suites and self-tests.
