# Game studio project-proposal contract and recovery

## Observed failure

Super Awesome Games accepted the official Shardfall pitch and hired its Director,
Creative Director and Producer. Producer 6.3.1 prepared a valid project proposal,
but the broker rejected `existingProjectId: null` as an unknown property.
`expectedProjectRevision: null` had the same mismatch. Both properties already
existed in `WorkstreamPlanProposalV2Request` for imported-project adoption and
were supported by the governance handler. Two handwritten catalog schemas omitted
them. The effective schema rejected the request before an approval or project
was created.

Director 0.4.4 repeatedly passed the terminal collaboration to
`RequireProgressingHandoff`, throwing on each attention review. The documentation
commitment reached its repeated-failure stop. The existing pitch/team remained
valid; another reset or larger model budget could not resolve this contract error.

## Contract boundary

- `ContractToolSchema.Create` exports serialized property names and types from
  the actual .NET wire contract, expands local references, and normalizes the
  schema to the broker validator's supported representation. It is shared with
  `WorkDeliveryToolSchemas.Input`, preserving that exporter behavior.
- `WorkstreamProposalToolSchema.Input` derives the proposal schema from
  `WorkstreamPlanProposalV2Request` and applies explicit business limits. The
  effective catalog routes to this single definition; the unreachable duplicate
  proposal schema is removed. Root `additionalProperties: false` remains enabled.
- New projects accept omitted or null adoption fields. Imported proposals accept
  the identity/revision pair. `WorkstreamGovernanceCapabilityHandler.ProposePlanAsync`
  still requires both values together and invokes `ImportedProjectAdoption.RequireAsync`
  for current organization, project state, manager and revision checks. Acceptance
  is not implied by schema validity. Proposal creation and its approval wake use
  the existing normal governance persistence path.

`WorkstreamProposalSchemaTests` sends the SDK's real serialized request through
the effective catalog schema. It compares property names against serialization,
rejects unknown fields and invalid types, and tests the actual governance handler
for new/adopted projects, mismatched pairs, stale/foreign projects, idempotent
proposal creation and the approval notification. This covers the integration
boundary that permissive fake-capability tests previously skipped.

## Director continuation

`EnsureProjectFoundationAsync` recovers a proposal ID retained in the authenticated
Producer transcript before evaluating terminal collaboration state. If an approved
proposal already created the correctly managed/team-bound project, it recovers
that exact identity without resubmitting or replaying approval.

`ProjectProposalRecovery.ContinueProjectProposalWaitAsync` persists `ProjectProposalWait`
for blocked, failed, cancelled or incomplete proposal collaborations. The wait is
part of the Director's existing operating state. `GameDirectorNextStep.Describe`
and the documentation commitment show its specific reason, so the callback can
complete normally while project creation remains pending. Identical waits do not
generate repeated state writes or reset failure budgets.

A narrow contract-recovery continuation is automatic only when:

1. The original participating Director/Producer, session, team and exact accepted
   pitch still match, and the session has remaining turns.
2. The authenticated Producer's retained rejection names an unknown root schema
   field. The current granted tool listing now declares that field and permits
   the template's serialized root names/types.
3. The exact pitch is still readable and accepted. A receipt bound to the session,
   target Producer, pitch, schema digest and expected revision is saved before the
   normal resume operation.

There is one recovery window per advertised schema. Lost resume responses reuse
the saved command; a current Active read settles an uncertain result. Another
blocked revision under the same schema remains an owned wait. Unknown errors,
cancelled sessions, missing grants, changed/revoked evidence and exhausted turn
limits never authorize a speculative restart. Normal broker checks remain final.

The cleared wait retains its observed session revision. `MergeConcurrentState`
prevents older callbacks from replacing a newer blocker or restoring an old wait
after an Active read. Project creation clears the proposal wait.

## Producer submission identity

`SpecialistAgent.ProposeProjectFoundationAsync` uses the first Producer submission
ordinal for the initial proposal, preserving legacy draft/command keys after a
resumed turn. It saves the complete final command before dispatch. A cold restart
or a remote commit with a lost response therefore replays the exact same request
and domain idempotency key. It does not regenerate accepted scope or create a
second project proposal because the conversation advanced to a later ordinal.

Explicit manager `RequestRevision` decisions keep their existing distinct
proposal-bound keys. Submitted-proposal indexing, exact pitch reads, accountability,
team/profile/authority bindings and manager approval remain required. A cached
command or queued resume is not evidence of project creation.

## Verification and rollout

Prepared releases: **Director 0.4.5** and **Producer 6.3.2**, both tested with public
NuGet SDK **5.12.0** and Work Management Contracts **5.3.0**, with sibling project
references disabled. No SDK or contracts source/package change is required.

Verification passes **353 Director tests**, **508 Producer tests**, and **94 platform
schema/governance/onboarding/delivery tests**, with no skips. Failure scenarios include
unchanged contracts, duplicate wakes, resume failure before commit, lost response
after commit, changed/revoked evidence, wrong participants, cancelled/limited sessions,
stale callback merges, missing local proposal indexing, later submission ordinals
and cold restarts. The matching AppHost build succeeds in isolated outputs.

Published-output self-tests, NuGet version/assembly checks, source-release guards
and Linux bundle verification are recorded with hashes in
`.artifacts/cool-games-review/proposal-flow-release-verification.json`.

Rollout:

1. Publish/install Director 0.4.5 and Producer 6.3.2 through Installed Agents.
2. Stop the current AppHost, rebuild its normal startup outputs, and restart C-Sweet
   so AgentHost advertises the generated proposal schema. The verified isolated
   build is verification, not a replacement for these running outputs. No migration
   or compute/network permission is needed; QA/Technical Director stay unchanged.
3. Verify the retained Super Awesome Games collaboration resumes once and produces
   exactly one proposal. Confirm the Director's actual decision and authoritative
   Workstream before claiming project creation. Reuse the accepted pitch and team.
4. If the old documentation commitment remains at its historical repeated-failure
   stop, an authorized human/reporting manager can retry that existing card after
   the fix. Do not weaken the general self-requeue protection or create a duplicate.
   Subsequent waits now complete their callback normally.

Live continuation remains pending publication/installation and the matching
platform restart. Source changes, local packages and successful tests do not update
approved installed payloads. No live database record, manifest or artifact digest
was patched during this correction.

## 2026-11-09: staffing authority narrowed by the model (Qwen Games)

Producer 6.4.1 submitted the Prism Break proposal with `game-producer` removed from
`authorityEnvelope.authorizedStaffingRoleKeys`. `ProposePlanAsync` rejected it: "The authority
envelope must include every active profile staffing role: game-producer." The Director's template
contained the role. The Producer prompt asked the model to narrow the list, `ValidProjectDraft`
only checked for a subset, and the draft was cached under `accepted-v2` and frozen under
`project-foundation-command:{session}:initial`, so retries replayed it.

Producer 6.4.2 (prepared, not yet compiled, tested or published):

- The model no longer returns staffing authority. The submitted envelope is always the template's.
- Cached drafts and frozen commands have the template envelope restored before dispatch. The
  idempotency key is unchanged.
- The model returns a key and a name per milestone; other milestone fields are merged in code.
- A platform `ValidationFailed` is returned to the model once as `evidence.platformRejection`
  under `{cacheKey}:platform-corrected` and a `:platform-corrected` command key. A second
  rejection blocks and quotes the platform message.

Recovery of the blocked collaboration is not automatic:

- The session is `Blocked`. `RecoverTransientFailuresAsync` only resumes `Failed` sessions.
- The Director's automatic contract-recovery window (above) only opens for a rejection that names
  an unknown root schema field. This rejection does not match, so the Director keeps its
  `ProjectProposalWait`.
- An authorized human can retry the session (`POST …/coordination-sessions/{id}/retry`,
  `canRetry: true`). The session's source kind is `Chat`, so `ResumeAsync` gives the next turn to
  the Director, which must hand it back to the Producer. Not verified against Director source.

Open gap: each proposal-contract incident has needed its own recovery rule. A rule that opens one
recovery window when a participating package version, the tool schema digest or the template
digest has changed since the blocked turn would cover this incident and the previous one. See
[model output contracts](agent-model-output-contracts.md).

## 2026-11-10: one board per game, opened before the project (increment 1)

Built and unit-tested on 2026-11-10, not yet verified on a live company: platform change in `WorkManagementCapabilityHandler`, Director 0.6.0.

- Director 0.6.0 `EnsureGameBoardAsync` (`GameBoard.cs`) opens an unbound team board when the staffing plan's team is approved and stores its id in `GameDirectorOperatingState.BoardId`. It needs team-scoped `work.board.create`.
- With a board, `EnsureProjectFoundationAsync` starts the proposal with `StartBoardCoordinationAsync` (idempotency key `project-foundation-board:{board}:{visionDigest}`, team-scoped `communication.coordination.start-board.v1`). The session is `SourceKind == "Board"`, so `ResumeAsync` returns a blocked, artifact-less Producer turn to the Producer. A denied, conflicting or invalid board start falls back to the chat collaboration and its existing key.
- `WorkManagementCapabilityHandler.AdoptPreProjectBoardAsync`: a `work.board.create` request that names a Workstream and a team adopts the team's single unbound, empty, standard board whose manager actively supervises that Workstream. It binds the Workstream, applies the Workstream's board profile and requested key, makes the caller manager, reconciles project board access and records the create receipt. Zero or several candidates, an unsupervised manager, or any existing work item fall through to normal creation. `ProjectBoardRequestAdoptsTheSupervisorsEmptyPreProjectTeamBoard` covers each case. The Producer's `EnsureProductionBoardAsync` is unchanged.

Limits of this increment:

- The board holds no cards before the project exists. A pre-project board uses `general-work.v1`; the game board profile comes from the Workstream profile, and a board that already holds work is not adopted. Leadership cards need the game board profile to be available without a Workstream.
- Automatic resume after a specialist update (`RecoverBlockedCoordinationCommitmentsAsync`) needs the Director's waiting commitment to carry `BoardId` and the session id or request key in its `PersonalTodoWorkContext`. It does not yet.
- The board opens at team approval, after pitch approval. A team does not exist when the owner first gives a direction.

## 2026-11-10: live result on QFN Games

- Producer 6.4.2 verified live. The proposal for "Shatterwave" was submitted at 10:39:14 PST on the first attempt and approved at 10:39:47; the collaboration completed in three turns. Project approval came 11 minutes after the company was created.
- The board-first increment was **not exercised**: the Director was still running 0.5.1, so no game board was opened and the proposal ran as a chat collaboration. Director 0.6.0 must be published and installed, with its two new grants approved, before a new company can verify it.
- The Producer's proposal call used 4,694 input and 16,678 output tokens, more output than the 10,410 on 2026-11-09 despite the smaller reply contract. Reasoning length, not the reply, is the cost; see `agent-token-budgets.md`.

## 2026-11-10: live result on Super Awesome Games (Director 0.6.0)

Board-first increment, verified live up to project approval:

- 10:48:25 PST the Director created the team board (`work.board.create` completed) 5 minutes after the company was created and before either hire arrived.
- 10:49:24 `start_board_coordination` completed; the proposal ran as a board collaboration (empty source conversation) and completed in three turns. The Director is a team member and its team grants bind, so the fallback was not used.
- The board is named `Game Pitch — "Prism Break"` with key `GAMEPI`. `ExtractWorkingTitle` returns the pitch heading before project identity inference has run. The Producer's adoption renames it to the project name.
- Adoption is **not yet observed**. The board was still unbound and Director-managed at 10:57.

New defect found, Producer 6.4.2 (not caused by the board change):

- After approval every Producer attention review failed at `read_delivery_plans` with "The project delivery grant is required: work.delivery.read.v1" and was dead-lettered: 10:51:11, 10:54:32, 10:55:02, 10:55:17.
- `SpecialistAgent.HandleAttentionReviewAsync` calls `ReconcileDeliveryCorrectionAsync` for each accountable project before it reaches `EnsureProductionBoardAsync`. The delivery grant is only added by `ProjectSetupService.ReconcileProjectBoardAccessAsync` when the project board is created, so the review can never reach the step that would grant it.
- Producer 6.4.3 (prepared, not compiled): a `Denied` delivery-plan read in `ReconcileDeliveryCorrectionAsync` is treated as nothing to reconcile.

Follow-up at 11:07 PST, same run:

- **Adoption verified.** The board the Director opened (`bf708b9f…`) is now `Prism Break`, key `VG28CBAD13D1`, profile `video-game-production-board.v2`, bound to the project and managed by the Producer. The company has one game board.
- The dead-lettered reviews stopped by 11:00:52, when `read_delivery_plans` began to succeed, while the Producer was still on 6.4.2. The board was reached through the Producer's planning path (`EnsureProductionBoardAsync` from the planning cycle), not through the attention review. Producer 6.4.3 removes the roughly nine minutes of failed reviews before that point; it was installed after the run had already recovered, so it is not yet verified live.
- Technical Director, Engineer and QA were hired at 11:00–11:01, 17 minutes after the company was created.

