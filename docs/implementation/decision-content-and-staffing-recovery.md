# Decision content and carried-forward staffing recovery

## Decision text

`WorkstreamGovernanceCapabilityHandler.RequestDecisionAsync` and `DecideDecisionAsync` use
`AuthoredText` for required decision content. `ExecutiveDecisionService.CreateAsync` and
`AnswerAsync` use the same content normalization for prompts, option labels/descriptions and owner
replies. Content is retained in full, without character caps, silent truncation or a decision-specific
metadata byte cap. Blank required content, JSON shape, option identity, revision and authority remain
validated. Bounded machine identifiers are distinct from authored content.

`CoreConfigurations` maps project decision summary/impact/rationale, gate decision rationale and executive decision
prompt/answer to PostgreSQL `text`. Staffing correction reasons, related decision comments and
cancelled-hire error details also use `text` so the correction reason can travel unchanged through
cancellation and replacement evidence. `McpToolCatalog`, HTTP contracts and services agree on these
content fields; there is no model instruction to shorten a valid decision to fit persistence.

Migration `UnboundDecisionContent` widens existing columns without rewriting existing decisions.
Its normal downgrade restores the old column types and can reject overlength data; do not downgrade
after writing longer content without first deciding how that content should be preserved.

The November 14 QFN Games Prism Break failure submitted a 3,790-character scope review. The broker
allowed 4,000 characters but `WorkstreamDecisions.Summary` allowed 2,048, producing a save failure and
HTTP 500. `Fixtures/prism-break-planning-decision.txt` preserves that exact text. The tests cover the
incident, much longer Unicode content, request/read/decision events, owner cards and full replies.
`DecisionContentPostgresTests` applies the real migration operations to the old PostgreSQL column
types and verifies existing values, long-content round trips and unbounded column metadata. Set
`CSWEET_COMPUTE_TEST_DATABASE` to an isolated local PostgreSQL server to run it.

## Staffing correction

See [the hiring lifecycle](features/hiring-suggestion-lifecycle.md#correcting-an-approved-unfilled-staffing-slot)
for `ApprovedStaffingLineage`, current approval identity/revision, authority, preserved employees,
durable notification and ordinary replacement approval. This is shared platform behavior and does
not impose a game-specific team or a new planning gate.

## Publish and recover Prism Break

Publish/redeploy C-Sweet UI, API and AgentHost and apply `UnboundDecisionContent` through the normal
migration path. No SDK, WorkManagement.Contracts or agent package changes are required for these
two fixes; Producer 6.12.4 already discovers corrections and submits normally reviewed replacements.

1. Refresh the company Overview. The carried-forward security/privacy recommendation should now
   offer **Request correction** to the owner/accountable manager.
2. Submit the intended correction, for example: "Remove the unfilled security/privacy position.
   Victor already provides the technical planning capability. Preserve current employees and
   justify any future staffing against the approaching work."
3. Let Gabriel submit the corrected staffing proposal and Marcus review it. The UI shows a pending
   correction while this happens. Existing hires, including Victor, remain in place.
4. On Gabriel's blocked **Reconcile scope, technical backlog and delivery staffing** card, select
   **Retry after review**. Publishing the platform fix does not silently override a user-reviewable
   blocked commitment. The same planning question can now persist in full and reach its authorized
   reviewer. Any genuine scope questions still need their normal decisions before dependent work
   proceeds; a successful retry is not itself proof that Sprint 1 has started.

Verify the correction completes, the incorrect hiring recommendation is cancelled, the planning
decision is recorded, and Sprint 1 receives actual scoped work. No live project records or installed
runtime payloads are patched as part of this change.

## Verification

The Release build and 179 focused regressions passed with no skips, including the isolated
PostgreSQL column migration and both hiring concurrency fixtures. The suite covers decision
schema/broker persistence, gate decisions, owner cards/replies, carried-forward correction
lineage, current-manager authorization, replacement approval, completed-hire preservation,
shared teams, Overview recovery and capability output contracts. Results are retained in
`.artifacts/planning-root-tests/planning-root-causes.trx`. No live recovery actions were performed.
