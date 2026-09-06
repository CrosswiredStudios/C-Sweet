# Adaptive game staffing and progressive planning

New game projects use Creative Director 1.5.0, Producer 2.2.0, Technical Director 2.2.0,
SDK 3.28.0 and Work Management Contracts 3.16.0. Production profile revision 4 is additive;
existing projects retain their pinned revisions and approved staffing plans.

## Behavior to test

1. Update/reimport the three agent packages and restart the updated C-Sweet host. The new
   communication and artifact-package read permissions must be included in their grants.
2. Start a **new** game project with the Creative Director. Propose a small single-platform
   maze-chase arcade game with procedural visuals, no multiplayer, and no story content.
3. Accept the brief. The first staffing proposal should contain **only the Producer**.
4. Fulfill that hire and approve the project/workstream proposal. A board and Producer handoff
   should appear without waiting for technical, art, audio, QA, or implementation hires.
5. The Producer should create a draft sprint and propose technical leadership. Once the
   Technical Director joins, its accepted-brief-grounded proposal should produce concrete tickets.
6. Leave implementation hires pending. The backlog and a bounded tentative sprint (up to eight
   tickets) should remain inspectable, with unassigned ownership visible and no invented capacity.
7. Fulfill one required role. Its existing tickets should gain eligible assignments without
   duplication. Missing-role tickets and their dependents remain unavailable for commitment;
   independent work can proceed through estimation and QA readiness.
8. Add a specialist requirement in a separate, larger game brief. Staffing should follow actual
   proposed work rather than a fixed fourteen-role roster. Unresolved creative/technical authority
   questions are escalated; an updated accepted brief is required before committing that scope.

The Producer owns staffing proposals; the Creative Director reviews scope/capability evidence.
Approved unfulfilled slots are not duplicated. Hiring/spending and public launch retain separate
platform approval. QA readiness is still required before executable scope becomes Ready: a draft
sprint is not permission to start execution.

## Local package verification

The validation feed is `artifacts/adaptive-packages` (git-ignored). These packages have not been
published. Configure that feed in any isolated build environment that cannot use the developer's
NuGet cache; publish the dependencies through the normal release process for remote installations.

From the C-Sweet repository, package-only agent verification uses:

```powershell
dotnet test ../CSweet.Agent.Producer.VideoGame -c Release `
  -p:UseLocalCSweetAgentSdk=false `
  -p:UseLocalCSweetWorkManagementContracts=false `
  -p:RestoreConfigFile=C:/Users/PC/Documents/GitHub/csweet/artifacts/adaptive-packages/NuGet.Config
```

Use the same flags for the Technical Director. For the Creative Director additionally set
`-p:UseLocalCSweetMemory=false`. Host verification uses Release output to avoid the running Debug
host's locked files. Restart the development host normally before interactive testing.

## Implementation boundaries

- Project staffing requires the Producer; detailed decomposition requires a technical lead.
- The accepted creative brief is sufficient design input when no dedicated Game Designer is needed.
- Technical planning produces a scope-specific typed proposal with validated roles, skills,
  acceptance criteria, parents, and dependencies. Invalid/cyclic proposals fail visibly.
- Roster changes rebind the existing backlog; they do not regenerate model proposals.
- Planning revisions can attach validated stage assignments. The broker checks optimistic
  concurrency, execution immutability, stage policy, explicit delegation requirements, owner,
  runtime eligibility, team roster and profile evidence before storing them.
- Draft tickets outside the estimated, dependency-consistent, QA-reviewed scope return to the
  unscheduled backlog before sprint commitment. Drafts with no accepted capacity cannot start.

Automated coverage includes Producer-only drafting, partial-role dependency selection, late-hire
assignment and replay, stale assignment rejection, contract compatibility, lean technical proposal
validation, and the existing Creative Director/SDK suites. Interactive end-to-end game delivery has
not been exercised by this change.
