# Repository directory

The business Source control page follows `docs/14-application-design-system.md`.
The approved design uses a repository table on desktop and stacked records on phones.

## Interaction decision

`RepositoryAccessPopover` opens only on click, tap, or keyboard activation. Hover and
focus alone must not open the access list. The popup supports Escape, an explicit
close button, and dismissal outside the popup. Its trigger counts agents only;
the popup lists both agents and humans with their access type and source.

## Data and meaning

- `SourceControlRepositorySummary.CreatedAt` is the persisted C-Sweet repository
  record creation date (the date added to C-Sweet for imported repositories).
- `RepositoryDirectoryProjection.PopulateAsync` enriches the authorized business
  repository list for `InternalRepositoryManagementService.ListAsync` and
  `SourceControlOnboardingService.GetDashboardAsync`. It batches queries rather
  than issuing one metadata request per row.
- Project links come from provisioning requests, work-item delivery specifications
  or development briefs, and existing workspace work items. A shared team by itself
  is not a project association. Multiple actual projects are shown rather than
  choosing an arbitrary one. An unlinked repository displays “Not assigned.”
- The latest activity compares the latest repository audit event with persisted
  workspace/publication updates. Actor names resolve from audit attribution or
  employee identities. The directory never returns audit metadata or credentials.
- Human C-Sweet members receive Read or Admin according to repository administration
  permissions. Agents receive **Team access** from current team memberships and
  enabled team repository policies. Disabled/retired installations, archived teams,
  ended memberships, and inactive/archived employees do not contribute to the count.
  Each employee appears once, with all applicable team names.
- Team access describes configured repository eligibility; individual Git operations
  still require assigned work, approved capabilities, and runtime authorization.
  The popup does not claim to enumerate external provider collaborators.
- Missing activity or creation dates are stated explicitly; no synthetic dates or
  example employees are inserted into production responses.

## UI and verification

`RepositoryDirectory` owns sorting, project links, timestamps and responsive rows.
`RepositoryDirectoryPresentation` formats activity labels and pluralized relative time.
`RepositoryAccessPopover` reuses MudBlazor's click-activated menu and portal behavior.
The portal container's shared styling lives in `wwwroot/css/app.css`; component
contents use isolated styles.

Regression coverage lives in `RepositoryDirectoryProjectionTests` and
`SourceControlWorkspaceRenderingTests`, alongside the existing internal repository
and onboarding service tests. Browser verification uses the actual shared Blazor
components at desktop and phone widths and checks that hover/focus do not open the
popup, keyboard/click do, and Escape/close/outside dismissal work.
