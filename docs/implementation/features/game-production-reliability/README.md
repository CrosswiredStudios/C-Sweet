# Game production reliability improvements

## Purpose

This document set turns the useful operational ideas identified in RoboCo into a C-Sweet-native
implementation plan. The objective is not to copy RoboCo or to make the C-Sweet core specific to
games. The objective is to make C-Sweet's existing game-production organization measurable,
traceable, and capable of improving from verified delivery outcomes.

C-Sweet already has the harder foundations:

- policy-pinned board orchestration;
- exact human and agent assignments;
- capability and scoped-action grants;
- hardware-isolated Office execution;
- immutable artifact revisions and review evidence;
- specialist video-game agents and a shared AgentKit;
- organization-, team-, role-, employee-, and case-scoped memory.

The missing pieces are operational closure: repeatable end-to-end evaluations, stable acceptance
criterion coverage, findings that survive revision cycles, a generated view of the effective
assignment contract, curated playbooks, and safe proactive production checks.

## Read these documents in order

1. [Architecture and design principles](./01-architecture-and-principles.md)
2. [Phase 1 - Golden-path evaluation harness](./02-phase-1-golden-path-evaluations.md)
3. [Phase 2 - Criterion coverage and remediation ledger](./03-phase-2-criteria-and-findings.md)
4. [Phase 3 - Generated effective assignment contracts](./04-phase-3-effective-assignment-contracts.md)
5. [Phase 4 - Curated playbooks and proactive programs](./05-phase-4-playbooks-and-programs.md)
6. [Phase 5 - Operator readiness and rollout](./06-phase-5-readiness-and-rollout.md)
7. [Junior developer delivery checklist](./07-junior-developer-checklist.md)

Do not start a later phase until the preceding phase's automated acceptance tests pass. Phase 1 is
intentionally read-mostly and can reveal defects in the existing workflow before new persistence
models are introduced.

## Delivery order and dependencies

| Phase | Deliverable | Depends on | Independently releasable |
|---|---|---|---|
| 1 | Deterministic game-production evaluation harness | Existing orchestration and game agents | Yes |
| 2 | Stable criteria and persistent review findings | Phase 1 baseline scenarios | Yes, behind additive APIs |
| 3 | Generated effective assignment contract | Phase 2 criterion/finding wire shape | Yes |
| 4 | Approved playbooks and program registry | Phase 2 evidence; Phase 3 contract context | Yes, feature flagged |
| 5 | Readiness diagnostics, dashboards, and controlled rollout | Metrics from phases 1-4 | Yes |

## Product boundaries

### In scope

- Generic work-management primitives that also support non-game businesses.
- Game-production fixtures and examples that prove those primitives under cross-discipline work.
- Deterministic orchestration and evidence assertions.
- Optional subjective evaluation that cannot override deterministic results.
- Explicit human/manager approval where work could cause material change.

### Out of scope

- A fixed game-studio organization chart in the C-Sweet core.
- Direct execution through the Docker socket or host credentials.
- A second memory or vector-search subsystem.
- Automatic production changes based only on an LLM judgment.
- Capturing private model reasoning or chain-of-thought.
- Copying source from RoboCo; its concepts must be implemented cleanly against C-Sweet contracts.

## Shared definition of done

The complete initiative is done when:

- a disposable game studio can deliver a small vertical slice through the real orchestration path;
- every parent acceptance criterion is owned and backed by evidence;
- review findings retain identity and history across revisions;
- an assignee cannot silently clear or verify its own blocking finding;
- every dispatched agent receives a generated contract that matches its pinned policy and effective
  grants;
- approved project playbooks can be recalled without crossing memory boundaries;
- proactive production programs deduplicate triggers and respect grants and approval policy;
- Office isolation and credential-free agent execution remain unchanged;
- all additive package changes are versioned, packed, and verified with sibling project references
  disabled.

## Primary existing extension points

- Board execution: `src/CSweet.Domain/WorkManagement/WorkOrchestration.cs`
- Orchestration service: `src/CSweet.Infrastructure/WorkManagement/WorkOrchestrationService.cs`
- Shared wire contracts: `../CSweet.WorkManagement.Contracts/src/CSweet.WorkManagement.Contracts/`
- Work planning: `WorkItemPlanningSpecification` in `WorkManagementContracts.cs`
- Artifact review: `src/CSweet.Domain/Core/Artifact.cs`
- Agent capabilities: `src/CSweet.AgentHost/Broker/`
- Memory integration: `src/CSweet.Infrastructure/Core/AgentMemoryService.cs`
- Game specialist base: `../CSweet.Agent.CreativeDirector.VideoGame/src/CSweet.VideoGame.AgentKit/`

These are starting points, not instructions to put every new behavior into those files. Follow the
component boundaries in the architecture document.
