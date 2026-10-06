# C-Sweet Board Work Orchestration Specification

Status: Normative  
Version: 2.0
Source inspiration: [OpenAI Symphony](https://github.com/openai/symphony/blob/main/SPEC.md)

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHOULD**, **SHOULD NOT**, and **MAY** in this document are to be interpreted as described by RFC 2119 and RFC 8174 when, and only when, they appear in all capitals.

## 1. Purpose

C-Sweet orchestration turns manager-approved sprints and explicitly activated project delivery plans into durable, observable work performed by assigned humans, agents, and trusted platform actions. Tasks execute within sprints; story, epic and release validation executes under delivery plans and can span sprints.

This specification and its [normative hierarchical delivery extension](../../docs/implementation/features/work-management/hierarchical-delivery.md) govern automated transitions. The extension defines new game/software workflow revisions, exact candidate evidence, aggregate acceptance and release recovery. Published V1 workflow revisions retain their original behavior. Assignment-event execution and generic move automations MUST NOT bypass either authorization model.

## 2. Trust and ownership

1. Every orchestrated board MUST have exactly one manager organization user. A manager MAY represent a human or installed agent.
2. Starting a sprint MUST be an explicit, audited action by that manager. A scheduler MUST NOT start a sprint implicitly.
3. Starting a sprint is the authorization boundary for task work. No task agent or trusted action MAY be dispatched for a Planned sprint. Aggregate work MUST require an activated delivery plan and MUST NOT fabricate a sprint identity.
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
- `MemberExecution`: work resolved per ticket to either an exact human or exact agent assignee;
- `ManagerApproval`: explicit decision by the board manager;
- `TrustedPlatformAction`: a registered platform-owned operation;
- `Terminal`: completed or cancelled end state.

Every stage key and outcome code MUST be a lowercase token matching `^[a-z][a-z0-9._-]{0,63}$`. A policy MUST have at least one Terminal stage, every reachable non-terminal stage MUST reach a Terminal stage, and every graph cycle MUST declare a maximum traversal count. The maximum traversal count MUST be between 1 and 10.

Publishing a changed policy MUST create a new revision. An Active or Paused sprint MUST remain pinned to its policy and approved delivery scope. Its board manager MAY revise assignments for stages that have never been dispatched, using current eligibility evidence, optimistic concurrency and an audited assignment-snapshot revision. Started and completed assignments MUST be retained. Changing a started assignment requires cancellation and replanning; it MUST NOT transfer an existing execution lease.

## 4. Assignments

Stage assignments identify exactly one of:

- a human organization user for ManualWork;
- an exact active agent installation for AgentExecution;
- an exact human organization user or active agent installation for MemberExecution;
- the board manager for ManagerApproval;
- a registered platform action for TrustedPlatformAction.

Executable leaf items MUST be rejected at creation if any reachable work or approval stage lacks a valid assignment. Initiative and Epic container items MAY omit assignments when they are not executable.

The platform MUST verify that an assigned installation belongs to the board organization, is active, and provides the published execution capability: `work.execution.run.v2` for hierarchical delivery or `work.execution.run.v1` for historical V1 policies. Assignments MUST NOT silently fall back to a role or capability pool.

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

AgentExecution MUST be enqueued as exact-installation capability work using its versioned assignment contract. V2 MUST identify Task, Story, Epic or Release scope, the authorized principal, planning/scope revisions, candidate evidence and permitted outcomes. Sprint identifiers MUST be present only for Task scope. Assignment events MUST NOT be used as an execution transport.
MemberExecution MUST use the same exact-installation transport when its ticket assignee is an agent,
and MUST enter the authorized manual-work state when its assignee is human.

## 7. Execution contract

Each attempt MUST use an idempotency key derived from sprint execution, item execution, stage, traversal, and attempt number. The assignment envelope MUST include those identifiers, the board and card identifiers, pinned policy revision, stage, attempt, deadline, instructions, item snapshot, validated stage input, prior outcomes, and evidence. Evidence MUST include the board manager's retry directions for the exact stage as `manager-direction` entries (the latest three), so a retried worker knows why it is being asked to try again. An assignee's own retry is not a manager direction.

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

`Blocked` MUST leave the item visibly blocked until the manager retries or cancels it. Because a Blocked stage is never retried automatically, the platform MUST notify the accountable manager on the first Blocked result instead of waiting for a repeated failure. The accountable manager is the board manager, or the agent's own manager when the agent manages the board itself (`AgentTicketFeedback.RecordFailureAsync` with `awaitingManager`). A worker whose blocker needs a management decision (scope, acceptance criteria, environment or tooling) rather than a retry or code change SHOULD include the diagnostic `decision-required:v1`. The escalation is then labelled as a decision. QA MUST NOT return `failed`, which routes work back to engineering, when its only gaps are criteria that no available role can verify. Cancellation MUST make outstanding inbox work ineligible, revoke attempt-scoped grants, and prevent late completion from advancing the item. Started assignments are immutable; future assignments use the manager-authorized revision path in section 3.

All scheduler state MUST be reconstructable from the database after process restart. Duplicate scheduler ticks, work claims, results, and manager commands MUST be idempotent. A stopped project stage MUST persist a recovery wake in the same transaction as its state change. The manager MUST re-read authoritative state before recovery; reconnect discovery MUST recover a missed wake. Missing staffing MAY be cleared after an authorized assignment repair without rerunning completed work. Other blockers MUST retain their evidence and use explicit retry, decision or replanning operations.

## 9. Hierarchical game and software delivery profiles

The new code-task workflow is:

`ready -> implementation -> technical-review -> trusted task integration -> quality -> done`

Technical Review MUST independently approve the exact source and story target before trusted task integration. QA MUST test the exact integrated story commit. Failed QA MUST return the task for a new reviewed fix based on current story state; merged history MUST remain intact.

Artifact tasks MUST deliver an exact artifact revision and digest and MUST receive independent criterion-level QA without requiring a Git repository. Every task MUST receive QA, including QA-authored deliverables. Accepted dependency documents MUST require task QA rather than task-level Producer acceptance.

Stories MUST require all pinned tasks, full candidate regression and trusted integration into the configured release or optional epic branch. Epics MUST require all stories, integration evidence and assigned Producer/project-manager acceptance. Releases MUST require all scoped epics, full release regression, technical readiness and explicit release-manager acceptance before every participating repository is promoted to its default branch. Deployment and public release are separate operations.

New workflows MUST NOT display standalone Merge or merge-decision columns. Producer Review and Manager Review MUST be epic gates. Sprint metrics and completion MUST count tasks independently of continuing parent validation.

Promotion MUST serialize each target, revalidate both source and target and retain durable per-repository receipts. Partial promotion MUST remain incomplete and MUST resume unfinished operations without automatic reversion. Unsupported provider guarantees MUST block with an actionable reason. Scope or candidate changes MUST invalidate affected readiness and acceptance. The extension specifies the typed API, event/outbox, recovery and clean-install requirements.

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
- policy and started assignments are immutable while active; future assignment edits are manager-authorized, audited, and race safely with dispatch;
- task integration verifies independent Technical Review and task QA verifies the resulting commit;
- artifact, story and release QA verify every required exact revision and criterion;
- aggregate acceptance, scope invalidation and partial multi-repository recovery follow the normative extension;
- tenant, grant, encryption, and workspace boundaries remain enforced.
