# Agent Orchestration

## Objective

New project delivery follows the normative [hierarchical delivery specification](implementation/features/work-management/hierarchical-delivery.md): sprint-authorized tasks, activated-plan aggregate reviews, exact QA evidence and durable promotion recovery. Merging does not grant deployment approval.

CSweet should use Microsoft Agent Framework as the initial execution runtime while keeping company state, policies, and contracts owned by the application.

The runtime reasons and executes. The application authorizes, persists, budgets, audits, and presents the work.

## Organizational hierarchy

```text
CEO
└── Personal Assistant / Chief of Staff
    ├── Department heads
    │   └── Team managers
    │       └── Workers
    └── Direct specialist workers where appropriate
```

Workers should normally report results to the manager that delegated the task. The Personal Assistant remains the primary communication channel to the CEO.

## Personal Assistant responsibilities

The Personal Assistant should:

- Interpret executive intent
- Clarify goals from available context
- Create proposals before executing high-impact work
- Determine required capabilities
- Inspect the current workforce
- Recommend staffing changes
- Delegate planning to project or department managers
- Monitor outcomes, budgets, issues, and deadlines
- Consolidate reports
- Escalate decisions that exceed delegated authority
- Maintain the executive inbox

The Personal Assistant should not personally perform every specialist task.

## Planning and delegation loop

1. Receive a CEO message or system trigger.
2. Classify it as a question, command, goal, correction, approval, or status request.
3. Create or update durable domain records.
4. Determine the capabilities required.
5. Ask the workforce router for an execution plan.
6. Create a task graph with explicit dependencies and acceptance criteria.
7. Reserve budgets and validate permissions.
8. Start eligible work.
9. Process worker results and events.
10. Route deliverables through review.
11. Escalate unresolved decisions or resource gaps.
12. Summarize outcomes to the CEO.

## Orchestration patterns

### Manager-as-controller

A manager retains responsibility and invokes specialists for bounded work. This should be the default pattern.

### Sequential workflow

Use when outputs must pass through ordered stages such as research, design, implementation, QA, and approval.

### Concurrent workflow

Use for independent tasks such as parallel research, alternative proposals, or implementation and documentation.

### Handoff

Use when responsibility genuinely transfers to another role. Handoffs should be recorded in task history.

### Review loop

A producer submits an artifact, a reviewer evaluates it against criteria, and revisions continue within configured limits.

### Human-in-the-loop wait

The workflow persists and pauses until an authorized person provides a decision, file, approval, credentialed review, or physical-world action.

## Workforce routing

`IWorkforceRouter` should choose among current staff and marketplace candidates.

Inputs include:

- Required capabilities
- Acceptance criteria
- Risk and reversibility
- Credential requirements
- Privacy classification
- Deadline
- Budget
- Availability
- Expected quality
- Local-first preference
- Prior performance
- Provider health
- Human response time
- Company preference

Output should be a transparent `WorkforcePlan` with:

- Selected resources
- Alternatives
- Cost estimates
- Review requirements
- Privacy implications
- Human involvement
- Rationale

## Confidence and escalation

Workers should report confidence, assumptions, and unresolved risks. Confidence is advisory, not authoritative.

Suggested policy:

- High confidence and low consequence: complete automatically
- Medium confidence and reversible action: complete and notify
- Low confidence: request specialist or human assistance
- High consequence: require approval regardless of confidence
- Credentialed or regulated action: route to an appropriately verified professional

A blocked worker should return an escalation package containing:

- Work completed
- Missing capability
- Unresolved questions
- Recommended resource
- Estimated cost
- Schedule impact

## Structured execution contract

All worker adapters should produce a validated result envelope.

```json
{
  "summary": "Implemented the inventory service and added tests.",
  "recommendedStatus": "Reviewing",
  "confidence": 0.88,
  "assumptions": [],
  "artifacts": [],
  "issues": [],
  "decisionRequests": [],
  "proposedTasks": [],
  "hireRequests": [],
  "usage": {},
  "recommendedNextAction": "Assign integration testing to QA."
}
```

Free-form narrative may accompany the structured result but cannot replace it.

## Context assembly

`IContextAssembler` should provide only the information required for the assigned task.

Context sources may include:

- Company policies
- Project decisions
- Task inputs
- Selected artifacts
- Relevant conversation summaries
- Tool schemas
- Worker instructions
- Permission grants
- Budget grants
- Provider data-disclosure notice

Context must be versioned and recorded in an input manifest for audit and reproducibility.

## Sessions and memory

Maintain separate sessions per worker assignment or durable workstream.

Do not use one universal session for the company.

Session continuity should never be the only storage location for decisions or task state.

## Durable execution

Long-running work must survive:

- Process restart
- Provider outage
- Approval delay
- Human response delay
- Tool failure
- Rate limiting
- Model-server restart
- Temporary authentication failure

Persist task and execution state independently of runtime checkpoints. Runtime checkpoints are implementation details used to resume execution, not the company’s sole record.

## Retries and idempotency

Every side-effecting tool must support an idempotency strategy.

The orchestrator should distinguish:

- Safe automatic retry
- Retry requiring a fresh quote
- Retry requiring renewed approval
- Non-repeatable action requiring manual intervention

Retrying a workflow must not:

- Send duplicate emails
- Create duplicate purchases
- Submit duplicate filings
- Recreate provider work
- Repeat payments
- Merge the same change twice

## Included initial workers

The prototype should begin with:

- Personal Assistant / Chief of Staff
- Project Manager
- Researcher
- Developer
- QA Engineer
- Editor

Additional role templates should be data-driven and installable later.

## Runtime abstraction

Suggested interfaces:

```csharp
public interface IAgentRuntime;
public interface IAgentFactory;
public interface IAgentRunCoordinator;
public interface IWorkflowFactory;
public interface IContextAssembler;
public interface IWorkforceRouter;
public interface IToolRegistry;
public interface IApprovalService;
public interface IExecutionEventPublisher;
```

Microsoft Agent Framework types should remain in an adapter project so the domain and application layers can evolve independently.

## Observability

Capture:

- Workflow and task events
- Model calls
- Tool calls
- Provider calls
- Token and monetary usage
- Latency
- Retries
- Warnings and failures
- Approval waits
- Human response times
- Provider outages
- Context sizes
- Evaluation results

The Blazor activity feed should present meaningful company events rather than raw model traces. An operations view may expose detailed diagnostics.
