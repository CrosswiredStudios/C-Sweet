# Phase 3 - Generated effective assignment contracts

## Goal

Generate one canonical, immutable description of what a particular assignee may and must do for a
particular stage attempt. Render that description consistently for agent context, operator UI,
SDK diagnostics, and conformance tests.

This is not a global role catalog. The contract is specific to the pinned execution and resolved
identity.

## Problem statement

Today the necessary information exists in several places:

- orchestration policy revision and stage definition;
- assignment snapshot and exact agent/human identity;
- work planning, criteria, and open findings;
- agent manifest capabilities;
- organization/board/resource scoped grants;
- broker tool descriptors and input/output schemas;
- evidence and artifact/source/build references.

If prompts, UI hints, runtime authorization, and SDK documentation summarize these independently,
they can drift. The effective contract projects existing authoritative data into one versioned
model. Runtime authorization remains authoritative and must still evaluate every operation.

## Canonical model

The proposed `EffectiveAssignmentContract` contains:

- schema version and deterministic digest;
- organization, board, sprint, item, stage, traversal, attempt, and assignment revision;
- pinned policy revision and stage type;
- exact subject kind and subject/installation ID;
- deadline, timeout, retry policy, and concurrency facts;
- instructions and input/output schemas;
- assigned criteria and relevant findings;
- readable evidence and immutable artifact/source/build references;
- effective capabilities with descriptor/schema digests;
- scoped resource grants and their revisions/expiry;
- legal outcome codes and their semantic meaning, but not caller-selected target stages;
- unmet preconditions and stable remediation guidance;
- generation timestamp and inputs digest.

Example excerpt:

```json
{
  "schemaVersion": "effective-assignment.v1",
  "stageExecutionId": "...",
  "assignmentRevision": 7,
  "policyRevisionId": "...",
  "subject": {
    "kind": "AgentInstallation",
    "id": "qa-installation-guid"
  },
  "criteria": [
    { "key": "AC-002", "claim": "Verifies" }
  ],
  "findings": [
    { "key": "FND-0042", "status": "Addressed", "blocking": true }
  ],
  "capabilities": [
    {
      "name": "work.finding.verify.v1",
      "descriptorDigest": "sha256:...",
      "scopes": ["board:gameplay", "work-item:GAME-17"]
    }
  ],
  "allowedOutcomes": ["verified", "changes_requested", "blocked"],
  "contractDigest": "sha256:..."
}
```

## Source-of-truth rules

| Contract field | Authoritative source |
|---|---|
| Stage and legal outcomes | Pinned policy snapshot |
| Assignee | Pinned assignment snapshot |
| Criteria/findings | Phase 2 records at assignment revision |
| Capabilities | Installed manifest plus broker capability catalog |
| Resource authority | Scoped grant resolver at dispatch time |
| Schemas | Versioned contract/capability descriptors |
| Evidence | Artifact/work/source/build records |

The builder must fail closed when a source is missing or inconsistent. Renderers must not query
these sources again or add facts not present in the canonical model.

## Services and renderers

```csharp
public interface IEffectiveAssignmentContractBuilder
{
    Task<EffectiveAssignmentContract> BuildAsync(
        Guid stageExecutionId,
        CancellationToken cancellationToken = default);
}

public interface IAssignmentContractRenderer
{
    string MediaType { get; }
    AssignmentContractRendering Render(EffectiveAssignmentContract contract);
}
```

Implement independent renderers:

- compact agent context in Markdown or structured JSON;
- operator lifecycle card/view model;
- SDK/diagnostic JSON;
- deterministic Markdown documentation for supported policy templates;
- test snapshot projection with volatile timestamps removed.

The builder depends on narrow policy, assignment, grant, descriptor, criterion, finding, and
evidence readers. It must not depend on UI or prompt classes.

## Digest and canonicalization

1. Normalize the canonical contract using deterministic property ordering and value formatting.
2. Exclude `GeneratedAt` from the content digest or derive it from the pinned dispatch event.
3. Sort unordered capabilities, scopes, criteria, findings, and evidence by stable keys.
4. Hash the canonical UTF-8 representation with SHA-256.
5. Persist the contract digest with the dispatch attempt and include it in completion evidence.

An outcome carrying a different contract digest is stale or malformed and must not advance the
stage.

## Capability projection rules

Effective capabilities are the intersection of:

1. capability declared by the installed agent package;
2. capability approved for that installation;
3. resource/action grant for the current subject and scope;
4. capability available through the current broker/Office connection;
5. any stage-level allowlist in the pinned policy.

Never show a tool in the agent rendering because its server happens to be installed. Never treat a
rendered capability as an authorization token. Normal broker authorization runs again at tool
invocation time.

## Preconditions and remediation

Return machine-readable preconditions:

```json
{
  "code": "blocking_findings_unverified",
  "satisfied": false,
  "resourceRefs": ["FND-0042"],
  "remediation": {
    "action": "verify_finding",
    "requiredCapability": "work.finding.verify.v1"
  }
}
```

The remediation describes the next permissible action, not a target stage. Trusted policy code
chooses the resulting transition after the action succeeds.

## Generated documentation and drift checks

For each built-in orchestration template, generate:

- lifecycle table;
- legal outcomes and bounded loops;
- required assignment kinds;
- input/output schema references;
- capability categories;
- validation fixture consumed by tests.

CI procedure:

1. Run the generator in check mode.
2. Compare generated output with checked-in output.
3. Run graph validators for reachability, terminal states, bounded cycles, and outcome coverage.
4. Run descriptor validators for unique capability names and resolvable schemas.
5. Fail with the exact stale generated files and regeneration command.

Generation is the only approved writer of generated files. Hand edits must fail CI.

## Implementation slices

### Slice 3.1 - Canonical builder

- Define the internal canonical model and input reader interfaces.
- Build it for an existing stage attempt.
- Add fail-closed validation and canonical digest tests.

### Slice 3.2 - Agent and diagnostic renderers

- Render compact assignment context.
- Add contract digest to dispatch and outcome correlation.
- Expose an authorized diagnostic read for operators.

### Slice 3.3 - UI projection

- Display assignee, policy/stage, criteria, findings, evidence, capabilities, and preconditions.
- Clearly distinguish effective authority from unavailable/requestable authority.
- Do not perform grant calculations in UI code.

### Slice 3.4 - Template documentation generator

- Generate lifecycle docs and validation fixtures.
- Add check mode and CI drift enforcement.
- Document regeneration for developers.

### Slice 3.5 - Agent integration

- Update the shared game AgentKit to consume the rendered contract.
- Remove duplicated role prompt text only after parity tests prove the generated context is present.
- Keep role expertise and creative instructions authored; generate only runtime facts.

If the AgentKit change requires an additive SDK surface, follow the SDK package versioning rule and
verify packaged consumers without local project references.

## Tests and acceptance criteria

- Identical pinned inputs produce byte-identical canonical output and digest.
- Changing a grant revision, criterion, finding, schema, assignment, or policy changes the digest.
- Changing unordered collection insertion order does not change the digest.
- An unauthorized capability never appears in any renderer.
- Removing a broker tool marks the precondition unsatisfied rather than inventing a fallback.
- Agent, UI, SDK diagnostic, and generated docs agree on stage and legal outcomes.
- Outcome with the wrong contract digest is rejected as stale.
- Generated lifecycle validation catches unreachable stages, unknown targets, missing outcomes, and
  unbounded review cycles.
- Rendered context contains no credential, secret value, host path, or hidden model reasoning.
- Phase 1 scenarios pass while asserting the contract digest at each agent stage.

## Completion gate

Do not use generated contracts to remove existing context assembly until one release has run both
paths and parity telemetry shows no missing criteria, findings, evidence, or capabilities.
