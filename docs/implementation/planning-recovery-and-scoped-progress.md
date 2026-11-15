# Planning recovery and scoped progress

## Incident and boundaries

The Qwen Games Prism Break run reached a project-bound board, a draft Production Sprint 1 and a
completed technical outline. `WorkIssueService.RequireBoardAsync` then rejected Gabriel's issue
reconciliation: it checked an OrganizationUser principal while his valid board planning grant belonged
to his current AgentInstallation. Repeating that failure correctly stopped his native planning task.
The manager mention did not produce owned follow-through. Separately, the outline covered the first
increment and omitted later approved outcomes; an ownership gap with no affected Epic indexes held
every Epic, including independent feasibility work.

These are separate permission, recovery and planning-impact problems. No new readiness gate,
mandatory specialist, game-feature inventory or automatic approval is introduced.

## Platform authorization and truthful failures

`WorkActorAuthorization.AuthorizeAsync` resolves an active current organization member to its current
installation principal, or its human principal. `WorkIssueService.RequireBoardAsync` and
`CanReadSourceAsync` use that same resolution. Existing accountable-manager hierarchy, project,
board, source, revocation and expiry checks remain in force. There is no fallback to an old installation
or another employee's grants. A manifest is not authorization.

`AgentTicketFeedback.FailureSentence` describes denied access without prescribing Manage members
as a universal repair. A manager can inspect recorded permissions and platform failure details.

## Shared manager recovery

SDK `ManagerWorkRecovery` is independent of specialist intake. `CSweetManagerAgentBase` uses subscribed
mentions as wake hints and recovers missed notifications on normal attention/reconnect review by
reading currently authorized personal-work boards. It discovers executable blocked work, including plan stages, on
directly reporting boards, including existing records and subordinate recovery escalations.

A durable episode is reserved before Add. The manager commitment references the original native
item, using stable correlation and command keys. Duplicate wakes, interrupted state writes and lost
Add responses converge on the same commitment. `AgentRuntimeWorker` dispatches these commitments
before role-specific claim policies and handlers.

`AssessWorkRecoveryAsync` supplies judgment: retry, continue investigation, or escalate, with evidence.
Code owns identities, scope, source revision and keys. Assessment and retry intent are persisted in
one operating-state write. A current reporting/revision read precedes requeue; the platform still checks
authority. Interrupted dispatch replays the retained command. An unchanged repeated failure does not
trigger another retry or repeated model inference; it requests assistance through reporting ownership.
Unfinished investigation follows the same assistance path rather than waiting for unassigned evidence.
Substantive board evidence or a new reviewer
version permits reassessment; routine timestamps do not. The standard hook is overridable for future
manager types.

Follow-up remains owned until current state shows the source is no longer blocked. A tracker finishing
does not mean the source delivery is complete. A blocked recovery escalates through native work and
reporting ownership; `OverviewIssueReader.ReadAsync` marks that recovery actionable for its reporting
owner. Ordinary unaffected team work is not promoted into a human action requirement.

## Complete outcomes, progressive detail

Technical Director `ReadRoadmapAsync` inventories the complete accepted product independently of the
requested increment. Later outcomes stay at Epic level; Stories and Tasks are refined near execution.
Existing accepted outline identity is retained rather than rewritten during upgrade.

Producer `ReviewOwnershipAsync` includes current staffing/resource facts in the review and cache
fingerprint. Its judgment can identify:

- `Scoped`: concrete affected Epic indexes; only those Epics and their real consumers are held.
- `FutureWork`: an explicitly independent later outcome, with no affected current Epic indexes. It
  stays open, receives an owner/commission, and permits independent current work to advance.
- `Unassessed`: impact remains unknown. Empty affected scope retains the conservative hold until
  judgment establishes actual impact.

`BindOwnership` rejects contradictory future/current impact. `OwnershipReconciliation` retains gap
identity, source evidence and stable commission keys. No missing obligation is discarded. Existing
completion checks in `ProgressivePlanning` and `FinalProductPlanning` still prevent project completion
while ownership gaps remain. Approved constraints and decisions can own obligations without copying
every passage into Epic acceptance criteria.

## Verification and rollout

Regression coverage includes real scoped human/installation permissions, revoked/expired/wrong-board/
wrong-organization/old-installation/inactive/non-accountable rejection, denied-command recovery and
issue dedupe. SDK tests exercise manager discovery, forged mentions, reporting and revision drift,
cached judgments and lost state/Add/retry responses. `PersonalTodoServiceTests` bridges SDK discovery
and handling to the real native task service: repeated failure is resumed by an installed manager,
source identity is retained, duplicate discovery converges, and unrelated ready work remains ready.
Producer tests retain future gaps, preserve genuine prerequisite holds and avoid duplicate roadmap
publication/refinement. Package-based tests and release artifacts are recorded under
`.artifacts/planning-recovery` (local verification, not deployment).

Final package-based verification: SDK 916, sample 2, generated template 7, Producer 581,
Director 449, Technical Director 252, and focused platform 221 tests passed. The optional
`WorkIssuePostgresTests.MigrationPreservesLegacyWorkAndInterruptedDispatchRollsBackSessionCardAndOutboxThenRecoversOnce`
test was skipped because its PostgreSQL fixture was not configured. Published-output self-tests,
NuGet versions/dependency pins, release-note identities, bundle verification and SDK assembly hashes
across the host/test output and all three agent bundles passed. `release-verification.json` records
package/bundle hashes, test counts and `deployed: false`.

Publish SDK **5.33.0**, then Producer **6.16.0**, Director **0.11.0** and Technical Director **6.9.0**.
Deploy the matching C-Sweet API/AgentHost/UI and update those installed agents through the normal
Installed Agents flow, including review of the subscribed mention event. No new contracts package or
database migration is needed for this change. Do not patch installed manifests, package records or
runtime payloads.

For retained Prism Break work, normal attention/reconnect discovery lets Marcus review and requeue
Gabriel's existing blocked planning task. A reasoned diagnostic retry can establish whether the
historical denial persists; the host correction authorizes the actual installation grant. Producer's
new planning-contract fingerprint refreshes ownership impact against current facts while keeping
the accepted brief, outline, draft Sprint 1 and existing work identities. Stable publication and
commission keys reuse existing work rather than recreate it. Fresh businesses use the same path.

After rollout, verify the original planning task advances, issue reconciliation succeeds, Epic tickets
appear, an approaching Story is refined, staffing is resolved for that selected work, and Sprint 1
receives executable tickets. Genuine gaps remain tracked or escalated. Tests prove the recovery
mechanism; live recovery is not confirmed until these records advance on the deployed version.
