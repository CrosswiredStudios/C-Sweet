# Current planning context and typed reply recovery

## Incident and cause

QFN Games / Prism Break reached an accepted brief, fulfilled hiring, one published Adaptive Feel
Experiment Epic and an empty planned Sprint 1. Producer 6.13.0 still ran in the business and repeatedly
failed state writes with `idempotency.content_mismatch`. Its planning advice finalization also fell
through to pitch refinement. Thirteen completed consultations carried the unrelated pitch-context
rejection. The accepted brief's statement that it granted no implementation/staffing approval was
then repeated as a prohibition in agent-authored management decisions, despite separately fulfilled
hires and verified Ready build capacity.

## Shared mechanics

- SDK `WorkContributions.IsReply`, `ReceiveAsync` and `ReadAnswerAsync` bind typed replies to the
  expected requester/specialist, current board manager and exact accepted project sources. No model
  call is needed to acknowledge a valid answer. Retained target answers survive an incorrect old
  requester finalization; the failed turn remains history.
- SDK `PlanningResourceContext.ReadAsync` supplies current roster, staffing request statuses and
  resource readiness independently of authored planning prose. Its substantive `Fingerprint` avoids
  timestamp/rewording loops. Requests, approvals, fulfillment and readiness are separate facts;
  neither this snapshot nor a model judgment grants execution permission.
- SDK `DecisionContinuations` follows explicit decision replacement chains and rejects cycles or
  other projects. It does not stop after an arbitrary number of replacements.
- Contracts `DecisionRecord.RequestedByOrganizationUserId` and `DecidedByOrganizationUserId` are
  nullable, platform-owned read fields. `WorkstreamGovernanceCapabilityHandler.MapDecision` supplies
  recorded authorship. Older host responses remain readable with unknown authorship. No migration.

## Agent behavior and recovery

Producer `HandleCoordinationTurnAsync` routes scoped advice before pitch intake. Ownership
reconciliation revalidates retained answers against accepted sources, retains real evidence
requirements, and uses durable grouped commissions from the preceding release. The planning
contract revision changes to `current-planning-context.v4`, allowing matching blocked work to be
reassessed once after update. `ReviewAdaptivePlanAsync` uses current resource context in its review
basis, permits draft selection before execution readiness, and rechecks context before applying
proposals. `RefreshReviewProposalsAsync` follows authoritative decision replacements instead of
presenting a cached obsolete terminal result.

Director `GeneratePlanningDirectionAsync` receives the current context, current decided project
directions and completed evidence. Planning-document acceptance and separate resource approvals
remain distinct. `ReassessPlanningContextAsync` can replace agent-owned escalation judgments through
normal decision supersession when facts or the review contract change. It never automatically
replaces a human answer, unknown-author decision, CEO hold, QA disposition or unrelated financial
decision. A still-reserved pending owner question stays pending. Reviewed context is recorded
durably; unchanged facts do not repeat judgment. Context drift during review prevents applying a
stale decision. Normal attention discovery resumes a pending replacement after interrupted writes
or missed events. Existing decision events wake Producer reconciliation.

No game-specific staffing list, new startup gate, fixed sprint count or blanket approval is added.
Agents retain judgment about scope, sequence and useful experiments. Existing execution staffing,
resource authorization, source validation and work-specific readiness checks continue to apply.

## Verification and rollout

Publish Contracts **5.9.0**, SDK **5.32.0**, then Producer **6.15.0** and Director **0.10.0**. Deploy the
matching C-Sweet host and use Installed Agents to update both agents. Shared helpers are available
for future agents; no installed artifacts or live business records are patched during development.

After deployment, the next attention/reconnect discovers retained planning work. Verify that current
resource/hiring facts are supplied, old agent-only escalations receive authoritative replacement
direction where appropriate, valid advice is reused, Stories/Tasks are published and Sprint 1 gains
estimated scope. A completed callback or successful retry alone does not prove the sprint started.
Actual human-reserved decisions still need their owner. Keep existing Prism Break; no reset is needed
by this recovery design. Live recovery remains unverified until the published updates run.

Regression reports and release artifacts are under `.artifacts/planning-context/`. Coverage includes
requester finalization, wrong key/source/speaker, current staffing/readiness, changed context,
retained Prism Break advice and decisions, interrupted replacement writes, unchanged replay, human
authority preservation, explicit long replacement chains, and independent drafting/progress.

Final local verification passed with sibling SDK/contracts references disabled for both agents and
the host: SDK 901, sample 2, generated-agent template 7, Contracts 82, Producer 576, Director 449 and
focused host 23 tests. One optional PostgreSQL migration/dispatch test was skipped because no isolated
database was configured; this change introduces no database schema migration. Both agent NuGet
packages and Linux release outputs pass version/source checks and published-output self-tests;
both `.csab` bundles pass verification. The host and both agent bundles contain SDK/Contracts
assemblies whose hashes match the newly packed packages. Exact versions, test totals and artifact
hashes are recorded in `.artifacts/planning-context/release-verification.json`.
