# Phase 4 - Curated playbooks and proactive programs

## Goal

Turn repeated, verified delivery lessons into approved procedural memory and run safe, configurable
production checks that propose work before problems are forgotten.

This phase contains two related but separate features:

1. **Playbooks** capture approved ways of working.
2. **Programs** detect conditions and create proposals or work through normal C-Sweet services.

Do not combine them into one service. A program may cite a playbook, but playbook approval and
program execution have different lifecycles and authorization.

## Part A - Curated project playbooks

### Reuse the existing memory platform

C-Sweet already supports `ProceduralMemory`, confirmation states, trust tiers, scoped namespaces,
operational references, recall feedback, and approved knowledge transfer. Build the product
workflow on these primitives. Do not create another embeddings table or retrieval service.

### Playbook lifecycle

Use these product states:

| State | Meaning |
|---|---|
| Draft | Proposed from verified evidence; excluded from high-trust briefings |
| Approved | Confirmed by an authorized project authority; eligible for recall |
| Archived | Retained for history but excluded from normal recall |
| Superseded | Replaced by a newer approved version |

Map approval to memory trust and confirmation deliberately. For example, Draft may be
`AgentInference/Pending`, while Approved becomes the configured trusted tier with `Confirmed`.
Avoid silently promoting memory because it was repeated by several agents.

### Promotion candidates

A candidate may be proposed from:

- the same verified finding pattern recurring across distinct work items;
- a successful resolution with objective before/after evidence;
- an approved retrospective decision;
- a manager-authored process;
- a program report with sufficient evidence.

The proposer supplies evidence references, applicability, proposed procedure, project/role scope,
and sensitivity. Trusted deterministic code validates references and deduplicates candidates.

### Example playbook

```json
{
  "name": "Verify particle budgets before arena integration",
  "applicability": "Godot combat scenes using more than three concurrent emitters",
  "procedure": [
    "Capture the approved performance profile.",
    "Run the particle-budget validation on the candidate build.",
    "Attach P50/P95 frame-time evidence.",
    "Escalate when the P95 budget is exceeded."
  ],
  "scope": {
    "organizationId": "...",
    "workstreamId": "game-project-guid",
    "roleKeys": ["technical-artist", "qa"]
  },
  "evidenceRefs": ["FND-0042", "build:sha256:new-build"]
}
```

### Recall rules

- Only Approved, currently valid procedures enter normal assignment context.
- Query within the organization and workstream first; broader organization knowledge requires an
  explicitly readable namespace.
- Filter by role/applicability before ranking.
- Include citations and version in rendered context.
- Fit within the assignment contract's token budget.
- Record whether the procedure was supplied, accepted, corrected, or rejected.
- Never allow a playbook to add capabilities or override policy.

### Services

Keep candidate management separate from memory storage:

```csharp
public interface IPlaybookCandidateService
{
    Task<PlaybookCandidate> ProposeAsync(...);
    Task<PlaybookCandidate> DecideAsync(...);
}

public interface IPlaybookRecallService
{
    Task<IReadOnlyList<PlaybookReference>> RecallAsync(...);
    Task RecordOutcomeAsync(...);
}
```

The candidate service validates provenance and performs approval. An adapter writes approved
procedures through the existing memory APIs. The recall service composes existing memory results
for assignment use.

## Part B - Proactive program registry

### Program definition

A program is a versioned configuration, not a hardcoded named background worker. It contains:

- organization and optional workstream/board scope;
- name, definition version, enabled state, and explicit opt-in;
- trigger kind and versioned trigger configuration;
- explorer identity/required capabilities;
- output schema;
- deduplication key strategy and cooldown;
- materialization action and approval policy;
- feedback fields to include in the next run;
- revision, creator, approver, and timestamps.

### Trigger types

Initial extensible trigger handlers:

- `schedule.v1`: due according to a stored schedule and time zone;
- `work-event.v1`: reacts to an eligible work-item activity type;
- `metric-threshold.v1`: compares a trusted metric against a threshold;
- `artifact-event.v1`: reacts to a submitted/accepted exact artifact revision;
- `build-event.v1`: reacts to an immutable build outcome.

Each handler implements one interface and returns a normalized trigger occurrence. The runner must
not contain trigger-specific business logic.

### Execution pipeline

Every program follows the same trusted pipeline:

1. **Trigger:** create or claim a unique occurrence.
2. **Explore:** collect only authorized facts and immutable evidence.
3. **Propose:** produce schema-valid findings, report, or work proposal.
4. **Decide:** apply configured deterministic and human approval gates.
5. **Materialize:** call the normal work-item, finding, artifact, or notification service.
6. **Learn:** record outcome feedback; optionally propose a playbook candidate.

Programs never write directly to work, artifact, finding, or memory tables.

### Identity and authorization

- Give every program a dedicated automation identity.
- Start with no grants.
- Require exact capabilities and scoped action grants during Explore and Materialize.
- Persist the authorizing grant ID/revision with every material output.
- Revocation takes effect at the next read or mutation, not only at program startup.
- A program cannot delegate or manufacture authority.

### Deduplication

The occurrence key should be deterministic from program/version/scope/source event or metric
window. Store a unique constraint on that key. Replays return the existing execution.

Example:

```text
performance-budget.v1/{program-id}/{build-digest}/{profile-id}
```

Do not use a model-generated title as a deduplication key.

## Initial game programs

### Build health

- Trigger: immutable build completed.
- Explore: build/test/log summaries and expected commit.
- Propose: finding or report for failures and regressions.
- Materialize: work finding; no source change.

### Playtest defect triage

- Trigger: playtest artifact submitted.
- Explore: structured observations and linked build.
- Propose: deduplicated findings mapped to criteria.
- Materialize: findings held for producer/QA confirmation when confidence is insufficient.

### Performance budget drift

- Trigger: metric threshold over a trusted profile.
- Explore: current/baseline frame-time evidence.
- Propose: blocking or non-blocking finding according to configured threshold.
- Materialize: no automatic code or asset mutation.

### Accessibility regression

- Trigger: build or UI artifact event.
- Explore: deterministic accessibility checks and approved criteria.
- Propose: criterion-linked findings.
- Materialize: review/manager gate according to severity.

### Asset provenance

- Trigger: asset package submission.
- Explore: source, author, license, and digest metadata.
- Propose: findings for missing or incompatible provenance.
- Materialize: block approval only through configured deterministic policy.

## Implementation slices

### Slice 4.1 - Playbook candidates

- Add candidate lifecycle and evidence validation.
- Add manager review surface.
- Write approved candidates through existing procedural memory APIs.

### Slice 4.2 - Scoped recall

- Add workstream/role/applicability filtering and token budgeting.
- Add citations/version to effective assignment rendering.
- Record usage outcome feedback.

### Slice 4.3 - Program registry and one trigger

- Add definition/execution/occurrence models.
- Implement schedule trigger, deduplication, and a no-op/report materializer.
- Add dedicated identity and grant checks.

### Slice 4.4 - Safe materializers

- Add adapters that call existing finding, work-item, artifact, or notification services.
- Add proposal/approval behavior.
- Prove programs cannot write domain tables directly.

### Slice 4.5 - Game program templates

- Add the five templates one at a time.
- Keep each disabled until explicitly enabled for a project.
- Run in observe-only mode before allowing finding/work-item materialization.

## Tests and acceptance criteria

- Pending or archived playbooks never enter normal assignment context.
- Recall cannot cross organization/workstream/role/sensitivity boundaries.
- Corrected or rejected playbooks stop being supplied after projection refresh.
- Replaying a trigger creates one occurrence and one material output.
- Revoking a grant prevents subsequent exploration/materialization.
- Recursive program-triggered work events do not create infinite program loops.
- A program cannot execute a materializer not declared by its versioned definition.
- All program outputs identify source evidence, program version, identity, and authorizing grant.
- Observe-only mode produces reports but no work items, findings, approvals, or artifacts.
- Phase 1 vertical-slice runs remain deterministic when programs are disabled.
- With one program enabled, its expected proposal is produced exactly once.

## Completion gate

No program may receive automatic materialization authority until it has completed an observe-only
period, demonstrated deduplication, passed security review, and been explicitly enabled by an
authorized project operator.
