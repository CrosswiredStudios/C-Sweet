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
