# C-Sweet Board Work Orchestration Specification

Status: Normative  
Version: 1.0  
Source inspiration: [OpenAI Symphony](https://github.com/openai/symphony/blob/main/SPEC.md)

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHOULD**, **SHOULD NOT**, and **MAY** in this document are to be interpreted as described by RFC 2119 and RFC 8174 when, and only when, they appear in all capitals.

## 1. Purpose

C-Sweet board orchestration turns a manager-approved sprint into durable, observable work performed by explicitly assigned humans, agents, and trusted platform actions. This specification adopts Symphony's reconciliation, deterministic dispatch, bounded retry, isolation, and observability principles while using C-Sweet's protocol-v2 agent runtime and Agent Work Inbox.

This specification is the sole authority for automated board transitions. The legacy delivery pipeline, assignment-event execution, and generic move automations are not part of this model.

## 2. Trust and ownership

1. Every orchestrated board MUST have exactly one manager organization user. A manager MAY represent a human or installed agent.
2. Starting a sprint MUST be an explicit, audited action by that manager. A scheduler MUST NOT start a sprint implicitly.
3. Starting a sprint is the authorization boundary for automated work. No agent or trusted action MAY be dispatched for a Planned sprint.
4. Every executable leaf item MUST have an accountable organization user and an explicit principal assignment for every reachable work or approval stage before it can enter a sprint.
5. The orchestrator MUST own all automated stage and card transitions. Agents MUST report progress and structured outcomes and MUST NOT start, move, complete, or choose the next stage of an automated card.
6. A human assigned to a ManualWork stage MAY complete that stage through an authorized manual operation. Manual work MUST participate in dependencies and sprint completion.
7. Privileged effects such as repository merge MUST execute as typed, trusted platform actions. Policies MUST NOT contain arbitrary host shell hooks.

## 3. Board identity and policy

Each board MUST have a unique uppercase key of 2-12 ASCII letters or digits, beginning with a letter. Each card MUST receive an immutable, monotonically allocated identifier formed as `{BoardKey}-{Sequence}`.

An orchestration policy consists of immutable revisions. A published revision MUST contain:

- a stable policy and revision identifier;
- stages with a key, name, stage type, optional board-column binding, instructions, input and output JSON schemas, timeout, concurrency limit, and retry policy;
- outcome-driven transitions;
- board, organization, global, stage, and assignee concurrency limits;
- a merge policy for software workflows;
- timestamps and the publishing manager.

Stage types are:

- `Queue`: non-executable waiting state;
- `AgentExecution`: exact-installation capability work;
- `ManualWork`: work completed by an assigned human;
- `ManagerApproval`: explicit decision by the board manager;
- `TrustedPlatformAction`: a registered platform-owned operation;
- `Terminal`: completed or cancelled end state.

Every stage key and outcome code MUST be a lowercase token matching `^[a-z][a-z0-9._-]{0,63}$`. A policy MUST have at least one Terminal stage, every reachable non-terminal stage MUST reach a Terminal stage, and every graph cycle MUST declare a maximum traversal count. The maximum traversal count MUST be between 1 and 10.

Publishing a changed policy MUST create a new revision. An Active or Paused sprint MUST remain pinned to the complete policy and assignment snapshot captured at start. A manager MUST cancel and replan the sprint to change that snapshot.

## 4. Assignments

Stage assignments identify exactly one of:

- a human organization user for ManualWork;
- an exact active agent installation for AgentExecution;
- the board manager for ManagerApproval;
- a registered platform action for TrustedPlatformAction.

Executable leaf items MUST be rejected at creation if any reachable work or approval stage lacks a valid assignment. Initiative and Epic container items MAY omit assignments when they are not executable.

The platform MUST verify that an assigned installation belongs to the board organization, is active, and provides `work.execution.run.v1`. Assignments MUST NOT silently fall back to a role or capability pool.

## 5. Sprint lifecycle

Sprint state is `Planned -> Active <-> Paused -> Completed | Cancelled`.

Only the board manager MAY start, pause, resume, cancel, or retry sprint execution. Start MUST perform one atomic preflight-and-commit transaction that verifies:

- actor, board, sprint, and policy authorization;
- no other Active or Paused sprint exists for the board;
- the published policy is valid;
- every executable sprint item is Ready and completely assigned;
- installations and capabilities are active;
- human stages have human assignments;
- dependencies are acyclic and point to completed work or work in the same sprint;
- cycles, WIP, and concurrency limits are valid.

On failure, the sprint MUST remain Planned and return stable, actionable validation errors associated with the policy, item, stage, or assignment. On success, the platform MUST persist the policy snapshot, assignment snapshots, sprint execution, item executions, initial stage executions, and manager audit event before dispatch can occur.

## 6. Reconciliation and dispatch

Every scheduler pass MUST reconcile durable executions before dispatching new work. Reconciliation MUST:

- ingest completed Agent Work Inbox results exactly once;
- expire or retry lost leases;
- block work whose authorization or installation became invalid;
- cancel work made ineligible by manager cancellation;
- advance manual and approval results;
- complete a sprint only when every executable item is terminal;
- record late results without applying them.

An item is dispatchable only when its sprint is Active, its current stage is executable, all dependencies are terminal-successful, it has no live attempt, its retry time has arrived, and all concurrency limits permit it.

Dispatch order MUST be deterministic:

1. Critical, High, Medium, Low priority;
2. ascending sprint rank;
3. ascending item creation time;
4. ordinal card identifier.

The scheduler MUST enforce configured global, organization, board, stage, and assignee limits plus the installation manifest's runtime concurrency. There MUST be at most one live attempt for a stage execution.

AgentExecution MUST be enqueued as exact-installation capability work named `work.execution.run.v1`. Assignment events MUST NOT be used as an execution transport.

## 7. Execution contract

Each attempt MUST use an idempotency key derived from sprint execution, item execution, stage, traversal, and attempt number. The assignment envelope MUST include those identifiers, the board and card identifiers, pinned policy revision, stage, attempt, deadline, instructions, item snapshot, validated stage input, prior outcomes, and evidence.

The result envelope contains:

- `Disposition`: `Completed`, `Blocked`, or `Failed`;
- `OutcomeCode`: a policy-defined lowercase token;
- `Summary`: a manager-safe summary;
- `Output`: JSON validated against the stage output schema;
- evidence and artifact references;
- manager-safe diagnostics.

The orchestrator MUST reject unknown outcomes, invalid schemas, and mismatched execution identifiers. A worker result MUST NOT name a target stage. Only the pinned policy maps an accepted outcome to a transition.

## 8. Retry, cancellation, and recovery

Lease, runtime, transport, and other transient infrastructure failures MUST retry at most five times. Delay is `min(10 seconds * 2^(attempt-1), 5 minutes)` plus bounded jitter. Deterministic validation, authorization, business, and worker failures MUST NOT retry automatically.

`Blocked` MUST leave the item visibly blocked until the manager retries or cancels it. Cancellation MUST make outstanding inbox work ineligible, revoke attempt-scoped grants, and prevent late completion from advancing the item. Reassignment of an Active snapshot is forbidden.

All scheduler state MUST be reconstructable from the database after process restart. Duplicate scheduler ticks, work claims, results, and manager commands MUST be idempotent.

## 9. Software delivery profile

The standard software profile is:

`ready -> development -> quality -> merge-decision -> governed-merge -> done`

Quality outcome `changes_requested` MUST return to Development. The default maximum is three development/quality traversals and MAY be configured from 1 to 10.

Development output MUST include repository connection, source branch, pull-request URL, and exact commit SHA. Quality MUST validate that exact SHA. Governed merge MUST revalidate the QA-approved SHA, repository authorization, branch protection, and merge grant.

Merge mode is board-configurable:

- `ManagerApproval` is the default and pauses at merge-decision;
- `Automatic` performs the same governed checks without the approval pause.

Only successful trusted merge MAY advance the card to Done.

## 10. Observability and security

The platform MUST retain durable sprint, item, stage, attempt, progress, transition, blocker, and audit records. APIs and UI MUST expose current stage, assignment, attempt count, progress, last activity, retry time, blocker or error, and outcome evidence without exposing secrets.

Logs MUST include organization, board, sprint, card identifier, execution, attempt, installation, and work-item correlation identifiers. Payloads and results MUST use the existing encrypted Agent Work Inbox. Agent capabilities MUST remain explicitly scoped and least-privilege. Repository workspaces MUST remain contained and collision-resistant under the existing workspace security and retention policy.

## 11. Conformance

An implementation conforms only if it proves through automated tests that:

- nothing dispatches before manager sprint start;
- start validation and snapshot creation are atomic;
- ordering and all concurrency limits are deterministic;
- dependencies and manual stages gate dispatch;
- agents cannot mutate automated transitions;
- retries distinguish transient from deterministic failures;
- cancellation, late results, duplicate ticks, lease expiry, and restarts are safe;
- policy and assignment snapshots are immutable while active;
- both merge modes verify the exact QA-approved commit;
- tenant, grant, encryption, and workspace boundaries remain enforced.
