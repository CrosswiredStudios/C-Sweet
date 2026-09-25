# Projects workspace

`Projects.razor` consolidates the project portfolio and work directories at
`/organizations/{organizationId}/projects`. The `view` query selects `portfolio`,
`boards`, or `personal`. Board and personal directory views load through
`WorkBoards.razor` independently of project inspection permissions.

## Project sections

The project route retains its project ID and uses `tab` for Overview, Efficiency,
People, Work, Documents, Communications, Decisions, Delivery, and Audit.
`ProjectPresentation.Tab` preserves the older `teams`, `governance`, and `evidence`
tab names. `ProjectResourceSection` groups the existing inspection records while
keeping their links, revisions, hashes, providers, and identifiers available.

The Work section embeds the existing `WorkBoards` workspace. Its `board`, `item`,
and `sprint` query parameters select an authorized project board and optionally
open a ticket or sprint. Board operations continue to use their own grants;
project membership does not confer board access.

`WorkBoards` also handles `/projects/boards/{boardId}`. The original `/work` and
`/work/boards/{boardId}` routes remain valid for existing links. The sidebar has a
single Projects entry, and switching organizations returns to a directory rather
than carrying a resource ID into another organization.

## Sprint planning

`WorkBoardWorkspace` exposes Add tickets for planned and active sprints.
`SprintScopeEditor` lists current sprint tickets and unfinished backlog tickets.
Users can add tickets or return them to the backlog; the ticket drawer also has
a Sprint selector. `WorkBoards.SetItemSprintAsync` uses the existing revision-
checked, idempotent assignment endpoint and refreshes authoritative board state.

Preflight & start remains the orchestration action: it validates the sprint and
then starts its execution. It requires the existing start grant, a planned sprint,
an unarchived board, and no active or paused sprint. Planning access and start
access remain separate. Starting execution can dispatch assigned agents.

## Portfolio health

`WorkstreamInspectionEndpoints` supplies `ProjectPortfolioItem.AccountableManagerName`,
`BlockedItems`, and `ReleaseReady`. Blocked counts exclude completed and cancelled
work. Readiness uses the same recorded release evidence as project inspection;
status, lifecycle phase, and absence of failures do not imply readiness.

## Verification

`ProjectsWorkspaceTests` covers the portfolio, section aliases, record metadata,
and directory access independent of inspection. `WorkBoardWorkspaceTests` covers
sprint visibility, scope separation, permissions, and archive restrictions.
`WorkBoardPageTests`, `ProjectSetupRenderingTests`, and `BusinessNavigationTests`
cover the reused board, membership, and organization-navigation behavior.
