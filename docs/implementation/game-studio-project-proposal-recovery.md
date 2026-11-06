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
