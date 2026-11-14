# Work recovery on Overview

## State and authority

`BlockedWorkAttentionReader.ReadAsync` projects canonical blocked work visible through
`IPersonalTodoService.ListAsync`. It does not infer a failure from a timer, normal dependency wait,
or model prose. Archived work is excluded; children of a displayed blocked plan are grouped under
their root. Each card retains the assigned employee and manager.

`BlockedWorkAttentionResponse.CanRetry` requires an executable, independently resumable item and
a current scoped `PersonalTodoActions.Requeue` grant. `BlockedWorkAttentionCard` submits the
existing revision-checked requeue command. The server remains authoritative and a stale or denied
command leaves an explanation; visibility is never a grant to retry. Opening the affected work
remains available when retry is not authorized. A retry does not approve staffing, spend, scope,
QA acceptance, or fulfillment of another dependency.

`OverviewAttentionItems.StandaloneBlockedWork` avoids a second card when the same native decision
already has a question or incident card. `PendingAgentQuestions` and `OverviewAttentionState`
share the eligibility/counting rules. Both reload on canonical `WorkBoardChanged` notifications;
reconnect discovery repairs missed notifications. A failed work-attention read prevents the empty
state from claiming that nothing needs attention. Existing approval and incident cards retain
their specific decision options.

## Owner pause

`WorkItemMutationEngine.ProjectChiefAssessmentAsync` projects a ready assessment to Backlog while
its agenda is paused by the owner. The assessment remains Ready in the agenda, so resuming restores
its eligibility. Completed assessments and genuine execution blockers keep their own state.
`ChiefLeadershipService.ReadAsync` reprojects existing records, repairing the older false Blocked
card without overriding the owner's pause or replacing accepted evidence.

## QFN Games incident, 2026-11-14

The fresh PRISM BREAK run exposed three separate problems:

- Evelyn's owner-paused coverage task appeared as Needs attention in Activity while the Overview
  decision queue was empty. Owner scheduling intent had been encoded as an execution failure.
- Gabriel correctly reasoned that technical planning was needed and QA could be deferred, but
  selected numeric role position 16. That position identified `game-security-privacy`, producing
  a staffing package inconsistent with his own stated work. `CoverageRoleCatalog`, kickoff
  assessment, and adaptive sprint reviews now use named capability selections. Code binds each
  named choice to its canonical role. New cache contracts prevent replay of positional choices.
- Marcus identified the mismatch but returned `justified: true` for the underlying capacity gap.
  Code interpreted that as approval of the submitted package. `StaffingJudgment` now asks whether
  the exact package is approved as submitted and separately records required changes. Approval
  with required corrections is invalid; valid corrections return to Gabriel. This imposes no
  fixed staffing bundle, game genre, or mandatory specialist consultation.

The original QA deferral was a legitimate manager revision, not a platform failure. Gabriel did
respond automatically at his recorded review deadline. Event receipt alone did not clear that
future deadline. This incident's source changes do not claim to remove that delay.

The actual model replies are retained in the Producer and Director test fixtures. Already approved
requests are historical authorization records: upgrading agents does not rewrite a misclassified
approval or silently hire its replacement. The existing incorrect hire recommendation needs to be
withdrawn/rejected and a corrected staffing proposal submitted through the normal manager flow.

## Release and verification

Deploy the platform change normally, and publish/update Producer 6.12.3 and Director 0.8.3 through
Installed Agents. No installed payloads, approved manifests, or stored approvals are patched.
No SDK or WorkManagement package API changes are required for this repair.

Regression coverage lives in `ChiefLeadershipServiceTests`, `BlockedWorkAttentionTests`,
`CompanyDashboardRenderingTests`, `OverviewAttentionStateTests`, Producer `NamedStaffingChoiceTests`,
and Director `ExactStaffingApprovalTests` and `InitialTechnicalLeadershipTests`.
