# Phase 2 - Criterion coverage and remediation ledger

## Goal

Make acceptance criteria and review findings durable, addressable records rather than strings that
can disappear inside planning or outcome JSON.

This phase is generic work-management functionality. Game-production examples are used because
they exercise cross-discipline delegation, build evidence, artifact review, QA, and playtesting.

## Current state

`WorkItemPlanningSpecification` stores acceptance criteria as strings. `ArtifactReview` stores a
review's findings as JSON. Software QA produces structured `QualityFinding` values and quality runs
store result JSON. These are useful inputs, but they do not provide one stable finding identity or
an explicit address/verify history across revisions.

## Proposed domain model

### Work acceptance criterion

A criterion belongs to one work item and one planning revision.

Required fields:

- `Id`: durable GUID used internally;
- `Key`: stable human-readable key such as `AC-001`, unique within the work item;
- `Description`;
- `PlanningRevisionIntroduced`;
- `Status`: `Active`, `Superseded`, or `Removed`;
- `SupersededByCriterionId`, when applicable;
- creation provenance and timestamp.

Changing wording after approval creates a successor criterion. Do not mutate historical wording
used by an active or completed execution.

### Criterion coverage claim

A claim states that a child item or orchestration stage is responsible for a criterion.

Required fields:

- parent criterion ID;
- responsible work item ID;
- optional stage key;
- claim type: `Owns`, `Contributes`, or `Verifies`;
- planning revision and assignment revision;
- actor/provenance;
- status and timestamps.

At least one active `Owns` claim and one verification path must exist for every active criterion
before execution starts. One stage may cover several criteria and one criterion may have several
contributors.

### Work review finding

Required fields:

- stable `Id` and display key such as `FND-0042`;
- organization, board, work item, and optional item/stage execution IDs;
- origin type: artifact review, software QA, game QA, playtest, accessibility, build, or program;
- review round and reviewer identity;
- severity and `IsBlocking`;
- status: `Open`, `Addressed`, `Verified`, or `Waived`;
- criterion IDs;
- expected behavior and observed behavior;
- location/reference data appropriate to the origin;
- exact evidence references, including artifact revision/digest, source commit, or build digest;
- created and updated revisions/timestamps.

### Finding transition record

Do not overwrite finding history. Append a transition for every action:

- `Recorded`;
- `Addressed` with resolution summary and new evidence;
- `Reopened` with verification evidence;
- `Verified` by an authorized independent reviewer;
- `Waived` by an authorized manager with rationale and expiry, if any.

The finding's current status is a projection of its transitions and may be stored for efficient
queries. The transition log is authoritative.

## State-transition rules

| Current | Action | Next | Who may perform it |
|---|---|---|---|
| none | record | Open | Authorized reviewer/program identity |
| Open | address | Addressed | Assigned implementer or accountable owner |
| Addressed | verify pass | Verified | Independent authorized reviewer |
| Addressed | verify fail | Open | Independent authorized reviewer |
| Open/Addressed | waive | Waived | Authorized manager under pinned policy |
| Verified/Waived | reopen | Open | Reviewer/manager when new evidence invalidates closure |

The service must reject skipped transitions. Deleting a finding is not supported.

## Example

```json
{
  "key": "FND-0042",
  "origin": "game.qa",
  "severity": "High",
  "blocking": true,
  "criterionKeys": ["AC-002"],
  "expected": "Vertical slice stays at or above 60 FPS on the approved profile.",
  "observed": "P95 frame rate is 47 FPS in arena combat.",
  "evidence": [
    { "kind": "build-digest", "value": "sha256:old-build" },
    { "kind": "performance-report", "value": "artifact-revision-guid" }
  ]
}
```

Addressing the finding appends:

```json
{
  "findingKey": "FND-0042",
  "expectedRevision": 1,
  "resolution": "Batched repeated particle draw calls and capped active emitters.",
  "evidence": [
    { "kind": "source-commit", "value": "corrected-commit-sha" },
    { "kind": "build-digest", "value": "sha256:new-build" }
  ],
  "idempotencyKey": "address-FND-0042-corrected-commit-sha"
}
```

The verifier must evaluate `sha256:new-build`; evidence from the original build cannot verify the
fix.

## Application services

Separate query, mutation, and policy concerns:

```csharp
public interface ICriterionCoverageService
{
    Task<CriterionCoverageReport> GetReportAsync(...);
    Task ReplaceClaimsAsync(...);
}

public interface IReviewFindingQueryService
{
    Task<ReviewFinding?> GetAsync(...);
    Task<IReadOnlyList<ReviewFinding>> ListForAssignmentAsync(...);
}

public interface IReviewFindingCommandService
{
    Task<ReviewFinding> RecordAsync(...);
    Task<ReviewFinding> AddressAsync(...);
    Task<ReviewFinding> VerifyAsync(...);
    Task<ReviewFinding> WaiveAsync(...);
}
```

Authorization remains in the normal scoped-grant policy layer. The command service asks that layer
for a decision; it does not infer authority from job title strings.

## Versioned public contracts

Do not add positional parameters to `WorkExecutionAssignmentV1` or `WorkExecutionOutcomeV1`.
Introduce additive V2 records:

- `WorkExecutionAssignmentV2` includes assigned criterion references and relevant open/prior
  findings;
- `WorkExecutionOutcomeV2` includes criterion results and proposed finding actions;
- dedicated finding read/address/verify/waive request and response records;
- stable capability names for each operation.

Support V1 and V2 during migration. Dispatch V2 only when the installed agent declares the V2
contract capability. Do not infer support from package version alone.

This phase edits `CSweet.WorkManagement.Contracts`, so it requires:

1. a minor package version increment;
2. matching documented, template, and test versions;
3. updated downstream package pins;
4. build, test, and pack of the package;
5. consumer verification with sibling project references disabled.

If the SDK adds typed finding methods or capability constants, increment `CSweet.Agent.SDK` in the
same change and verify its consumers under the same rules.

## Persistence and migration

Use normalized tables for criteria, claims, findings, and transitions. JSON is appropriate for
typed evidence metadata, but not for fields used to authorize, join, filter, or enforce lifecycle
invariants.

Required database constraints/indexes:

- unique work item + criterion key;
- unique finding display key within its board or organization sequence;
- unique finding + idempotency key for transitions;
- indexes for organization/work item/current status and stage execution/current status;
- foreign keys for criterion references and execution provenance;
- check constraints or application validation for legal enum/status values;
- optimistic concurrency revision on mutable projections.

Migration strategy:

1. Create new tables without changing current JSON columns.
2. New planning writes create first-class criteria and retain the current V1 JSON representation.
3. New reviews dual-write first-class findings plus legacy JSON where an old consumer requires it.
4. Backfill only active/recent work where identity can be derived safely; mark ambiguous historical
   entries as legacy evidence instead of inventing identities.
5. Move reads to the new projection after parity tests pass.
6. Remove legacy writes only in a separately planned breaking change.

## Orchestration gates

- Preflight fails if an active criterion has no owner or verification path.
- Assignment creation includes only criteria relevant to that item/stage.
- Resubmission includes all open and addressed findings relevant to the stage.
- A completion outcome cannot traverse to success while a blocking finding is Open or Addressed.
- Stale verification against an older assignment, artifact, source commit, or build is ignored or
  rejected with a stable conflict.
- Maximum traversal and retry policy remain authoritative even when findings remain open.

## Implementation slices

### Slice 2.1 - Criteria and coverage read model

- Add criterion and coverage entities, mapping, migration, and query service.
- Create criteria from newly approved planning specifications.
- Add coverage report and preflight validation.
- Do not change assignment wire format yet.

### Slice 2.2 - Finding ledger domain

- Add finding and transition entities.
- Implement legal transition and idempotency unit tests.
- Add query and command application services using existing authorization abstractions.

### Slice 2.3 - Review adapters

- Map new artifact review and QA output into the ledger.
- Preserve original typed review JSON as evidence.
- Add fingerprint/deduplication behavior without merging distinct criterion/build findings.

### Slice 2.4 - V2 execution contracts

- Publish V2 records and capabilities.
- Add broker and SDK adapters.
- Negotiate V1/V2 explicitly per installation.
- Update all required package versions and pins.

### Slice 2.5 - Orchestration and UI gates

- Include criteria/findings in assignments.
- Enforce blocking rules.
- Display coverage and finding history on work-item/execution views.
- Keep authorization decisions server-side.

## Tests and acceptance criteria

- Stable criterion keys survive child decomposition and stage retries.
- Preflight names every uncovered criterion.
- Duplicate finding submissions return the original finding.
- Addressing does not imply verification.
- An implementer without verify authority receives authorization denial.
- Failed verification reopens the same finding rather than creating a replacement.
- Verification evidence must match the submitted revision/build.
- A waiver records manager identity, rationale, authorizing grant revision, and policy revision.
- V1 agents continue to run during migration; V2 agents receive structured criteria and findings.
- Cross-organization reads and mutations return no resource data.
- Phase 1 scenario C passes using first-class records rather than parsing outcome JSON.

## Completion gate

Do not begin generated contracts until V2 assignments are stable and a complete review loop can be
replayed without losing criterion, finding, transition, or evidence identity.
