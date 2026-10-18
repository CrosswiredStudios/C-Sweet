# Projects workspace

`Projects.razor` consolidates the project portfolio and work directories at
`/organizations/{organizationId}/projects`. The `view` query selects `portfolio`,
`boards`, or `personal`. Board and personal directory views load through
`WorkBoards.razor` independently of project inspection permissions.

The portfolio header links to All boards (`?view=boards`). Project boards are reached
from the project Work section; personal boards live in employee details. Existing
directory URLs remain supported, including the project Work section's All boards fallback.
`Projects.razor` presents real inspection data in Aurora cards with a clamped
outcome, expandable full brief, delivery readiness, phase, target, manager link,
and health links. Search and expanded filters retain their original behavior.

`AppearanceState` stores the browser preference under `csweet.appearance`.
`Appearance.razor` exposes the selector at `/settings/appearance`. `MainLayout`
loads it after interactive render and applies `data-appearance`; shared
`.aurora-page` tokens in `app.css` style Overview, the Projects portfolio and all project detail tabs,
and the layout attribute styles navigation. Page-local theme selectors are removed.
Storage failures keep the choice for the session and show a persistence notice.

## Project sections

`ProjectOverview` provides linked health counts, an expandable brief, board/team
navigation, release readiness and recent changes. Routine successful read activity
is omitted from the overview; `ProjectPresentation.IsRoutineRead` also powers the
Audit tab's optional read-event filter. Failed reads remain visible. Full event
identifiers, hashes and correlations remain in expandable audit details.

`ProjectResourceSection` adds name/type/status search, group counts and sorting by
attention, recency or name. Unavailable technical fields are omitted rather than
rendered as repeated placeholders. Dates are formatted for display; resource
deep links remain authoritative. `ProjectReleases` leads with recorded readiness,
the delivery manager, accepted epic counts and a next action.
`ProjectReleases.Presentation` derives conservative labels from current plan/check
state; no blockers does not imply readiness. Open findings and current blocked-check
reasons are deduplicated into Current blockers, with the manager accountable for
resolution. Active checks name their assigned owner and next action; blocked-check
recovery belongs to the manager. Past findings and completed, cancelled or superseded
checks are retained under closed History disclosures. Superseded is not described
as resolved. `ProjectReleaseEvidence` keeps exact commits, documents, repository
promotion receipts and review outcomes behind technical disclosures. Scope,
staffing and cancellation controls sit under Release scope and settings; exact
candidate review and activation/pause/resume retain their existing principals.
Recovery forms are expandable. `Projects.razor` also collapses the supporting
builds, previews and evidence directory by default. Existing realtime updates remain.

`Projects.razor.css` scopes Aurora styling to the embedded Work and Efficiency
components. The Work board keeps its original commands and grants. In
`WorkEfficiencyPanel`, a missing selected project/item shows an empty state rather
than company totals. Measurement context is expandable; project views omit company
overhead and label the existing export explicitly as a business report.

The project route retains its project ID and uses `tab` for Overview, Efficiency,
People, Work, Documents, Communications, Decisions, Delivery, and Audit.
`ProjectPresentation.Tab` preserves the older `teams`, `governance`, and `evidence`
tab names. `ProjectResourceSection` groups the existing inspection records while
keeping their links, revisions, hashes, providers, and identifiers available.

The Work section embeds the existing `WorkBoards` workspace. Its `board`, `item`,
and `sprint` query parameters select an authorized project board and optionally
open a ticket or sprint. Board operations continue to use their own grants;
project membership does not confer board access.

`CurrentActivityNavigation.WorkItemHref` links project tasks directly to
`projects/{projectId}?tab=work&board={boardId}&item={itemId}` for both the activity
card and provider popover. `WorkBoards.OpenBoardAsync` uses the project Work section
for project boards. Standalone legacy `work/boards/{boardId}` and
`projects/boards/{boardId}` routes resolve the authorized board and replace the
current history entry with that project URL, retaining `item` and `sprint`.
Boards without a project retain the standalone workspace; personal activity links
retain the owner's employee personal-board tab.

When embedded in a project, `WorkBoardWorkspace` starts with its view tabs and
board controls instead of repeating the project title or a "Project board" label.
`WorkBoardWorkspace` combines sprint selection, completion, search, attention,
display options, and primary actions in one desktop toolbar; controls wrap on
smaller screens. Completion exposes sprint dates in its tooltip and accessible
label. Add tickets, Execution, Refresh board, and permitted board settings share
the overflow menu. Full goals remain in the sprint directory. Cards omit the
repetitive execution-stage summary; execution details remain in the ticket pane,
while blockers and document revision links stay on cards. Workflow names and
counts appear in the columns themselves without a second navigation strip.

`WorkBoards` keeps its temporary ticket drawer mounted so opening and closing
can animate. `WorkBoards.razor.css` sizes it to `min(1180px, 92vw)`, uses the full
viewport below 800px, and places requirements beside delivery controls on desktop.
The header and close footer stay visible while the body scrolls. The closed pane
is inert with its focus trap disabled; reduced-motion preferences disable sliding.

`WorkItemDetailsContent` implements the Editorial presentation inside that drawer.
Its Overview tab prioritizes the outcome and numbered acceptance criteria, with
requirements, constraints, and the architecture digest in native disclosures.
The delivery sidebar retains the authorized sprint control, approvals, current
stage, and exact revision links from `WorkEvidenceLinks`. Discussion reuses
`WorkItemComments`; Activity reads the existing collaboration endpoint on demand.
Visited tabs stay mounted to preserve comment drafts. The host retains estimate,
move, retry and more-actions commands, and refreshes the pane on board events and
reconnect. Revision requests are scoped to the selected ticket and sprint, and
late responses cannot replace a newer selection's references or activity.

`WorkBoards` also handles `/projects/boards/{boardId}`. The original `/work` and
`/work/boards/{boardId}` routes remain valid for existing links. The sidebar has a
single Projects entry, and switching organizations returns to a directory rather
than carrying a resource ID into another organization.

## Sprint planning

`WorkBoardWorkspace.VisibleColumns` omits an empty Backlog workflow column from
active and paused sprint views, including the stage navigation. A Backlog column
with sprint tickets remains visible; search and attention filters do not change
this decision. Planned and historical sprints and All work retain every column.

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
