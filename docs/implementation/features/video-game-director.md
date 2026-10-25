# Video game portfolio leadership

`CSweet.Agent.Director.VideoGame` is the CEO-direct portfolio leader. In Game Studio mode the
owner's selected focus determines the leadership workflow. Creative & Product recommends
`game-director`; Research, Financial, and Legal & IP assess their respective functional leadership
needs without requiring a Director. Creative Director and Producer are the Director's shared reports.

## Code map

- `ChiefLeadershipService.SaveFocusAsync` readies the selected assessment. `SnapshotAsync` retains ordinary waiting, blocking and priority rules; it never promotes Creative & Product ahead of a different selection. `ReadAsync` repairs only untouched legacy Director-first assessment state from the saved noncreative focus, retaining accepted decisions and hires.
- `ChiefLeadershipService.AssessAsync` accepts `game-director` only for Creative & Product. Research accepts research-lead/research-director/chief-science-officer, Financial accepts finance-lead/financial-controller/chief-financial-officer, and Legal & IP accepts legal-lead/legal-counsel/chief-legal-officer for agent-appropriate coordination. Licensed-human requirements remain excluded from agent hiring. Candidate availability, funding and existing hiring authority remain checked by the hiring pipeline.
- `ChiefLeadershipService.AdvanceAsync` presents the next-focus decision after the selected area resolves. It never readies the next backlog domain automatically. `CaptureAsync` authenticates the new choice or Nothing else for now; `ReadAsync` recovers lost answers and only untouched automatic scheduling. Completed areas stay out of suggestions, and repeated reads do not duplicate cards. A direct new owner priority cancels the obsolete next-focus card.
- `ChiefOfStaffAgent.InterpretCoverageAsync` proposes the Director deterministically only for Creative & Product. Other focuses receive their own bounded assessment with matching allowed role keys; a Director response for another focus is rejected. `GameStudioOwnershipPolicy` delegates Producer team design to an active or pending Director across all games.
- `VideoGameDirectorAgent` persists concepts and approved projects separately. `PortfolioChat.cs` resolves explicit game context; personal agenda work retains concept identity even after the conversation moves to another game.
- `ProjectApprovalReview.cs` binds Producer setup to the stored exact CEO-approved pitch. `CreativeDesign.cs` commissions and reviews the Creative Director's GDD. `CeoReporting.cs` persists cadence, format, timezone, report drafts and scheduled occurrences.
- `VideoGameCreativeDirectorAgent` owns GDD creation/revision, creative work and status evidence. It has no hiring, project-approval or artifact-decision grants.
- Producer `ManagerChat.cs` prevents chat-driven project creation and uses explicit workstream context for shared portfolio work. Formal foundation coordination remains the approved setup path.
- `ProjectSetupService.GamePortfolio.cs` allows shared studio participation only for approved version-7 game projects with a current installed Director supervisor and explicit active team membership. Revoked grants and removed participants remain revoked/removed.
- `AgentEmployeeIdentityResolver.ReadProjectAssignmentAsync` omits a default project when an employee belongs to several active projects. Explicit work context determines scope.

The Director is the formal studio-team lead and approves resource changes; the Producer is the operational delivery and board manager. The Creative Director is a creative peer of the Producer. CEO approval of each official pitch is mandatory, including delegated involvement. Material deviations return for executive direction; launch and publication keep separate human gates.

## Releases and validation

Packages: Director 0.1.0, Chief 2.15.2, Creative Director 3.0.0, Producer 5.0.0. The role split targets a fresh installation; old role hierarchy state is not migrated. The focus-routing patch repairs only untouched queued assessments, preserving current hiring commitments. Deploy published releases through Installed Agents, with the host changes for role catalog, focus routing and shared-project access. Local tests and packages are verification artifacts only.

Regression coverage includes exact pitch gating, two leadership roles, independent concepts, replay-safe creative submission, CEO reporting schedules and DST, project setup review, shared-team access checks, and existing staffing/production regressions.

## Selected-focus routing verification (Chief 2.15.1)

The mode supplies business context; the selected focus supplies the initial leadership path.
This supersedes the Director-first rule introduced in Chief 2.15.0. No operating-mode inference,
owner pause, hiring authority, approved package record or live business state is changed.

Verification with sibling dependency references disabled:

- 164 deterministic Chief regressions passed (`focus-routing.trx`).
- 78 Chief/platform/DI checks passed, including PostgreSQL and recovery of untouched legacy
  Director-first scheduling (`focus-routing-platform.trx`).
- Fourteen real Chief/broker workflow cases exercise native focus, matching-role recommendations,
  lost acknowledgments, fulfillment and continuation, including all four Game Studio focuses.
- Seven synthetic local Qwen model cases passed (`selected-focus-model-final.trx`). Research,
  Financial and Legal chose research-lead, finance-lead and legal-lead respectively. Each final
  case used one model call; existing managers stayed covered and licensed legal work stayed a
  human-required question. These are interpretation evaluations, not installed/UI acceptance.
- The first model run truncated legal-coordination output at the 3,000-token allowance on both
  bounded attempts. Increasing the bounded allowance to 6,000 fixed this; all seven cases passed
  on the final implementation. No incomplete initial run is counted as acceptance evidence.
- Chief 2.15.1 was built, packed, inspected and its Linux bundle and published-output self-test
  passed. The installed package must be updated through Installed Agents together with the
  matching platform routing correction. Candidate availability and policy still determine
  whether a recommendation can become an actual hire.

## Owner-selected continuation (Chief 2.15.2)

The next-focus handoff supersedes automatic backlog advancement from earlier leadership workflows.
A fulfilled Director hire is reassessed for actual coverage before the next card is created. Creative
& Product is then excluded; Research, Financial and Legal & IP remain suggestions requiring a new
owner choice. The last remaining area is paired with Nothing else for now; an exhausted agenda
creates no extra card. Manager-authored approved staffing plans retain their separate authority.

Verification with published dependency pins: 165 Chief regressions, 83 platform/DI checks and all
14 Chief/broker workflow cases passed, including PostgreSQL, reconnect recovery, duplicate wakes,
shrinking choices, stale command rejection, pause/resume and atomic completion/card/message/outbox
rollback. Chief 2.15.2 was packed and its Linux bundle and self-test passed. Installed/UI acceptance
is pending. No live business state or existing Research recommendation was changed; accepted
hiring or native decisions are retained for explicit review rather than silently cancelled.
