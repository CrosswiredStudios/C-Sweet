# Meaningful planning ownership and scoped reconciliation

`SpecialistAgent.ReviewOwnershipAsync` in the Producer reviews distinct approved outcomes and
binding constraints. `OwnershipCatalog` supplies exact Epic outcomes, criteria, technical constraints,
approved decisions and reviewed `WorkContributions` advice. The model judges whether these actually
own an obligation; code supplies identity, scope and evidence text. A restriction does not need a
duplicate Epic criterion. A generic promise cannot replace a concrete outcome or its verification.

`PlanningScopeOwnership.BindEvidence` and `BlockedScopes` in SDK 5.30.0 are reusable mechanics.
They do not prescribe a genre, roster or sprint count. A binding establishes planning responsibility,
not implementation, acceptance, altered authority or permission to weaken criteria. Unknown impact
conservatively holds all supplied scope until clarified. Known impact holds only that scope, its
descendants and transitive consumers; prerequisites and independent work remain eligible.

## Who acts on a finding

`SpecialistAgent.ReconcileOwnershipFindingsAsync` separates three situations:

- An existing owner may have been missed: Gabriel asks Victor to explain the existing binding.
- A responsibility is genuinely absent: Victor proposes scoped planning responsibility and Gabriel
  reviews it against the accepted inputs. Neither a new Epic nor changed product criteria is required.
- Binding requirements actually conflict: exact incompatible requirements accompany a `work-planning`
  question to the accountable manager. Unresolved reviewed advice also goes to that manager. The
  existing authority policy determines whether a later decision needs the business owner.

A conditional instruction to escalate if an FPS observation proves impossible is not evidence that
it failed. The normal response is to name responsibility and retain the condition. User intervention
is reserved for actual decisions beyond delegated authority, with evidence and actionable choices.

`PlanningFinding.StatusSummary` includes the finding, affected work names, accountable actor, next
action and whether a user decision is requested. Protocol keys and indexes belong in diagnostics.
Stable finding and coordination identities make replay idempotent. Advice is accepted for review only
from the expected board, initiator, specialist, completed session and exact accepted document revision.
Changed outlines invalidate advice. Resolved or superseded findings become inactive; tracking cards
complete even if their obsolete session previously stopped. Advice stays in the audit history.

Legacy criterion-only ownership feedback also stays in the audit history. It is not replayed as
approved manager direction; only current source-bound decisions can supply that authority.

## Progress and recovery

`AdvanceProgressivePlanningAsync` publishes the roadmap and selects independent scope while affected
drafts are retained. `PublishCanonicalBacklogAsync` withholds affected executable leaves.
`OwnershipHeldItems` binds holds to current proposal provenance and propagates them through the board;
`CanPrepareWithPlanningDecisions` applies them before estimation/readiness and sprint admission.
Existing staffing, workflow and QA acceptance checks still apply. Outstanding obligations prevent final
completion even if no further candidate can be selected.

`PlanningContractChangedAtAsync` records a durable contract revision per project and accepted planning
fingerprint. Discovery can reassess a matching blocked commitment once when this contract is first
seen or changes. The timestamp is retained across reconnects; an unchanged later failure does not
create another retry loop. Ordinary revised evidence, decisions and approved amendments still use
their existing recovery paths. This is source-based recovery, without live database or runtime edits.

## Authored text

`PersonalTodoService` and `PersonalWorkPlanning` retain complete authored titles, descriptions,
criteria, waiting/blocking reasons, completion summaries and plan-task evidence. `McpToolCatalog`
matches service validation. `CoreConfigurations.ConfigureWorkTask` uses PostgreSQL `text` for titles,
reasons and summaries; migration `UnboundPersonalWorkContent` widens existing titles and summaries
without trimming them. Decision content remains covered by `UnboundDecisionContent`. Identifier,
enumeration, claim, revision and authority checks are preserved.

## Verification and rollout

Producer `OwnershipProgressTests` and `OwnershipReconciliationTests` cover independent progress,
transitive holds, draft retention, hypothetical versus evidenced conflicts, source-bound advice,
idempotent commissions, inactive tracking cards, manager choices, admission and no incomplete closure.
SDK `PlanningScopeOwnershipTests` cover shared bindings, graphs and human status. Platform
`PersonalTodoReportingTextTests` exercise 6,787- and 100,000-character reports, long personal plans and
task evidence through broker schemas and services. `DecisionContentPostgresTests` executes both
widening migrations on an isolated PostgreSQL database, preserves existing content, round-trips long
Unicode text and checks the model snapshot.

Publish SDK 5.30.0, deploy C-Sweet with its migrations, then publish/update Producer 6.13.0 through
Installed Agents. Existing accepted briefs, employees and drafts are retained. On the next authorized
attention/reconnect, the changed contract can release a matching blocked planning commitment once.
For the current Brickwave run, verify new ownership review, roadmap publication, scoped reconciliation
if needed, then actual estimation/staffing/readiness and Sprint 1 admission. A successful retry alone
does not prove the sprint started. Historical Prism Break recovery follows the same pattern.

Final verification passed: 884 SDK solution tests, 566 Producer solution tests, 156 focused platform
regressions (including both PostgreSQL migrations), and seven generated-agent tests. The Producer
suite and host build also passed with sibling SDK/contracts references disabled against an isolated
package cache. Both Producer self-tests passed, NuGet metadata matches SDK 5.30.0 and Producer 6.13.0,
and the linux-x64 release bundle passed verification. Test reports remain under the repositories'
`.artifacts/ownership-*` directories. No business records or installed runtimes were modified.
