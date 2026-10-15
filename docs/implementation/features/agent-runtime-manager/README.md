# Agent Runtime Manager

> **Superseded historical design:** This feature plan describes the removed Docker container
> runner. Use the [agent isolation implementation plan](../../../../Documentation/Architecture/CSweet_Untrusted_Agent_Isolation_Implementation_Plan.md)
> and [security architecture overview](../../../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md)
> for the provider-neutral, hardware-virtualized runtime.

## Goal

Add an Agent Runtime Manager that can run both always-on agents and scheduled ephemeral agents.

When complete, a user should be able to:

1. Open the Agents page.
2. Click **Import Agent**.
3. Paste a public GitHub URL for a .NET agent project.
4. Preview the root `csweet-agent.json`.
5. Approve bounded permissions and container limits.
6. Configure a tick frequency.
7. Let C-Sweet clone, build, package, and schedule the agent.
8. Have C-Sweet start the agent in a container on each tick.
9. Let the agent connect to the broker, do its work, report completion, and exit.
10. Have C-Sweet stop and clean up the container until the next tick.

The key design rule is:

> Installed agents are metadata. Running containers are temporary runtime instances.

## Feature docs

1. [Architecture](./01-architecture.md)
2. [Global Configuration](./02-global-configuration.md)
3. [Phased Implementation Plan](./03-phased-implementation-plan.md)
4. [Junior Developer Checklist](./04-junior-developer-checklist.md)

## Relationship to GitHub Agent Import

This feature is the runtime half of GitHub agent import. The import feature handles source,
manifest, approval, and package identity. The runtime manager handles activation, scheduling,
container lifecycle, limits, heartbeats, completion, and cleanup.

Use this plan together with
[GitHub Agent Import](../github-agent-import/README.md).

## Non-goals for the first version

- Kubernetes support.
- Multi-host scheduling.
- Private repository imports.
- Automatic marketplace billing.
- Running arbitrary non-.NET projects.
- Giving imported agents raw database credentials, Docker socket access, secret-store access, or
  any path that bypasses the broker.

## Definition of done

- Always-on agents can be started, stopped, and observed.
- Scheduled agents run only when their configured tick is due; OnDemand agents wake for durable subscribed work.
- Each tick creates at most one active runtime instance per installation unless configured
  otherwise.
- Ephemeral containers stop after completion, timeout, cancellation, or failure.
- Global agent/container settings are editable from the Configuration page.
- Imported .NET agents are built in an isolated builder workflow, not on the application host.
- Runtime instances can only interact through the broker and approved platform adapters.

## OnDemand attention and agent defaults

`AgentAttentionScheduler.DispatchDueReviewsAsync` discovers enabled, ready, active installations
with an approved `AgentAttentionEvents.ReviewDue` subscription. Startup, due periodic reviews and
state invalidations can enqueue attention for sleeping OnDemand installations without a broker
session. Scheduled installations still wait for their configured runtime activation; AlwaysOn
startup remains owned by `AgentRuntimeManager.EnsureAlwaysOnRuntimesAsync`.

Each attention inbox insert commits with schedule advancement in one transaction. PostgreSQL
dispatchers serialize each schedule with an advisory transaction lock; the existing schedule
concurrency token protects competing invalidations. An outstanding unexpired pending review or
leased review with an unexpired deadline or live lease coalesces later ticks. New invalidations
remain recorded until that review settles; expired work without a live lease permits a fresh
current review. Failed installations do not stop discovery
for the rest of the roster. `EnsurePendingOnDemandRuntimesAsync` rediscovers inbox demand after
missed activation, reconnect or capacity exhaustion. Review events remain hints: callbacks reread
authorized current state, and existing claim/lease rules govern execution.

The configured attention interval is retained. Routine completed attention reviews do not extend
the idle lifetime (`AgentRuntimeManager.ReleaseIdleRuntimeAsync`), while pending/leased work still
protects execution. OnDemand does not mean periodic duties cease; capacity savings depend on the
review cadence, cold-start cost and actual work.

New package defaults: Chief of Staff 2.13.2, Creative Director 2.1.2, Technical Director 3.1.2,
Producer 3.1.3, Software Architect 1.0.2, Software Product Manager 3.1.2 and YouTube Manager
(`com.csweet.agent.platform.youtube`) 0.3.1 use OnDemand. The older YouTube Account Manager and
Discord retain AlwaysOn. No SDK or contract-package changes are required.

Deploy/restart the platform with this scheduler before applying the new agent packages. A package
default does not rewrite existing installation schedules: change each selected installation using
`AgentInstallationService.UpdateScheduleAsync` (the Agents schedule controls), retaining its grants,
enabled state, runtime budget and overlap policy. Normal fenced idle shutdown releases its slot.
Do not convert the older YouTube Account Manager or system communication providers. Publishing,
installed-package upgrades and a real Office sleep/wake round trip are separate rollout checks.

Regression coverage: `AgentAttentionSchedulerTests` exercises sleeping startup/due reviews,
invalidations, coalescing and expiry; `AgentRuntimeManagerTests.SleepingAttentionReviewIsRediscoveredAndQueuesOneOnDemandRuntime`
connects scheduler demand to runtime discovery. Existing idle-release tests cover lease protection
and reviews not extending runtime lifetime.

October 15 verification: all 106 focused attention/runtime/platform-event tests passed, including
real PostgreSQL rollback and concurrent retry against disposable databases. All 1,071 tests across
the seven agent repositories passed with sibling package references disabled; all seven self-tests
and packs passed, with NuGet metadata versions and matching release headings inspected. Platform
tests used the existing sibling Memory source because the checkout's pre-existing
`CSweet.Memory.Broker` 0.5.1 pin is not published; the SDK, work-management, Office and isolation
dependencies used packages. Verification used isolated `OnDemandFinal` build outputs and did not
restart services or modify live installation schedules. No package pins were changed by this work.
