# Video game portfolio leadership

`CSweet.Agent.Director.VideoGame` is the CEO-direct portfolio leader. The Chief of Staff recommends `game-director` first for `game-studio`; Creative Director and Producer are shared direct reports.

## Code map

- `ChiefLeadershipService.SnapshotAsync` prioritizes the creative-product assessment for game studios. `AssessAsync` permits `game-director` there and preserves media-content's `creative-director` role.
- `ChiefOfStaffAgent.InterpretCoverageAsync` proposes the first Director deterministically. `GameStudioOwnershipPolicy` delegates Producer team design to an active or pending Director across all games.
- `VideoGameDirectorAgent` persists concepts and approved projects separately. `PortfolioChat.cs` resolves explicit game context; personal agenda work retains concept identity even after the conversation moves to another game.
- `ProjectApprovalReview.cs` binds Producer setup to the stored exact CEO-approved pitch. `CreativeDesign.cs` commissions and reviews the Creative Director's GDD. `CeoReporting.cs` persists cadence, format, timezone, report drafts and scheduled occurrences.
- `VideoGameCreativeDirectorAgent` owns GDD creation/revision, creative work and status evidence. It has no hiring, project-approval or artifact-decision grants.
- Producer `ManagerChat.cs` prevents chat-driven project creation and uses explicit workstream context for shared portfolio work. Formal foundation coordination remains the approved setup path.
- `ProjectSetupService.GamePortfolio.cs` allows shared studio participation only for approved version-7 game projects with a current installed Director supervisor and explicit active team membership. Revoked grants and removed participants remain revoked/removed.
- `AgentEmployeeIdentityResolver.ReadProjectAssignmentAsync` omits a default project when an employee belongs to several active projects. Explicit work context determines scope.

The Director is the formal studio-team lead and approves resource changes; the Producer is the operational delivery and board manager. The Creative Director is a creative peer of the Producer. CEO approval of each official pitch is mandatory, including delegated involvement. Material deviations return for executive direction; launch and publication keep separate human gates.

## Releases and validation

Packages: Director 0.1.0, Chief 2.15.0, Creative Director 3.0.0, Producer 5.0.0. This split targets a fresh installation; old runtime state is not migrated. Deploy published releases through Installed Agents, with the host changes for role catalog and shared-project access. Local tests and packages are verification artifacts only.

Regression coverage includes exact pitch gating, two leadership roles, independent concepts, replay-safe creative submission, CEO reporting schedules and DST, project setup review, shared-team access checks, and existing staffing/production regressions.
