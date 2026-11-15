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
requests remain historical authorization records. `StaffingCorrectionService` now gives the owner or
accountable manager a **Request correction** action on an unfilled recommendation. It atomically stops
the disputed recommendation and unfinished hire operations, records the exact direction, invalidates
older pending proposals, and queues a durable wake for the original staffing author. Fulfilled hires
and unrelated recommendations survive.

Producer `StaffingCorrections` discovers pending corrections on events and reconnect. A separate
personal commitment works before or after kickoff. The Producer reassesses named capabilities, removes
only disputed unfilled capacity from the approved baseline, and binds the replacement to the correction
identity/revision and exact owner evidence. `ResourceChangeService` checks that binding and preserves
fulfilled positions. Marcus reviews the exact new proposal normally; approval completes the correction,
supersedes the old plan, and enables ordinary hiring fulfillment. The actual roster then releases planning.

Use Overview's **Request correction** on the incorrect slot, describe the intended work/capability, and
choose **Submit correction**. Overview retains an in-progress status after the old hiring card disappears.
The replacement hire then follows the configured manual or automatic hiring policy. No copied approval,
direct data edit, business reset, or chat retry is part of this recovery.

## Release and verification

Deploy the platform change and its `AddStaffingCorrections` migration normally, and publish/update SDK
5.29.0, Producer 6.12.4 and Director 0.8.3 through
Installed Agents. No installed payloads, approved manifests, or stored approvals are patched.
The SDK adds typed correction discovery and optional proposal binding. WorkManagement contracts remain unchanged.

Regression coverage lives in `ChiefLeadershipServiceTests`, `BlockedWorkAttentionTests`,
`CompanyDashboardRenderingTests`, `OverviewAttentionStateTests`, Producer `NamedStaffingChoiceTests`,
and Director `ExactStaffingApprovalTests` and `InitialTechnicalLeadershipTests`.
