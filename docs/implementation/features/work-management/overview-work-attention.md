# Work recovery on Overview

## State and authority

The **Waiting** section shows one card for each open blocker the current user can see. **View affected work** opens its linked tasks and manager reports. Pending questions and useful, authorized retries move the same blocker into **Needs your attention**. The navigation badge counts that blocker once.

`OverviewIssueReader.ReadAsync` groups records by their case, decision, collaboration, explicit dependency or source task. It follows manager recovery tasks back to their source, including earlier recovery tasks. Independent blockers in the same project retain separate cards. Only work from authorized personal views and case boards appears in the disclosure. The overview re-reads on work, incident and decision changes and reconnect.

`BlockedWorkAttentionReader` uses `WorkRecoveryService` to check the failed operation against current facts. Restored access, a supported alternative or an agent update after the failure can justify a retry. The assigned user or reporting manager also needs a current requeue grant on the work item's board. A missing input, a management review or an unknown failure stays in Waiting. A pending decision takes precedence over retry controls on the same card.

`BlockedWorkAttentionReader.RetryAsync` rechecks the current revision and evidence before requeuing. The accepted retry and its evidence key are recorded together. If the same attempt fails again on the same facts, the overview waits for something to change.

`AppRealtimeCoordinator` shows routine snackbars. `OverviewIssueReader` uses the current collaboration state to show execution failures to active managers in the source chat, or the owner when the source chat has no active manager. The failure and linked work share one card and one badge item. **Review collaboration** opens the transcript and its recovery controls. A durable wake refreshes the card on failure and recovery. Document reviewer reassignment appears until its review notification is resolved.

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
