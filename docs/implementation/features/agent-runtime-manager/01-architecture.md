# Agent Runtime Manager - Architecture

## Summary

The Agent Runtime Manager decides when and how agent workloads run. It should be separate from the
broker.

The broker governs:

- Identity.
- Registration.
- Granted capabilities.
- Event and capability routing.
- Completion messages.
- Policy enforcement at the message boundary.

The runtime manager governs:

- Import materialization.
- Build/package lifecycle.
- Always-on process lifecycle.
- Scheduled tick lifecycle.
- Container creation and teardown.
- Runtime resource limits.
- Heartbeats, timeouts, retries, and cleanup.

Keep those responsibilities separate. The broker should not become a container orchestrator, and
the runtime manager should not decide what an agent is allowed to publish or request.

## Target flow

```text
Agents page
  -> Import Agent
  -> GitHub URL
  -> preview root csweet-agent.json
  -> admin approves grant + runtime settings + tick frequency
  -> import service clones approved commit
  -> isolated builder creates runtime image/package
  -> scheduler records next tick

Tick due
  -> runtime manager claims schedule
  -> runtime manager starts container
  -> agent connects to CSweet.AgentHost
  -> broker validates installation grant
  -> agent performs work
  -> agent publishes completion event or exits
  -> runtime manager records outcome
  -> runtime manager stops/removes container
  -> scheduler computes next tick
```

## Runtime modes

### Always-on

Use for first-party or high-priority agents that must respond immediately.

Examples:

- Personal Assistant.
- API gateway principal.
- Future coordinator agents.

Behavior:

- Runtime manager starts the workload during platform startup.
- If it crashes, restart according to policy.
- Health is based on broker registration heartbeat plus process/container state.
- It remains running until disabled, updated, or the platform shuts down.

### Scheduled ephemeral

Use for agents that wake up on a schedule, do bounded work, then stop.

Examples:

- Inbox summarizer.
- Research scanner.
- CRM hygiene worker.
- Daily planning worker.

Behavior:

- User configures tick frequency.
- Runtime manager starts one container per due tick.
- Agent has a max runtime.
- Agent reports completion to the broker.
- Container exits or is stopped.
- Runtime manager records result and schedules the next tick.

### On-demand ephemeral

Use for one-off test runs and admin-triggered work.

Behavior:

- User clicks **Run Now**.
- Runtime manager starts an instance immediately.
- Same limits and completion semantics as periodic ephemeral.

### Warm pool

Do not implement in the first version. Add later for frequently used, trusted agents.

## Agent manifest additions

The current `csweet-agent.json` supports executable metadata. Add a source-build-friendly runtime
shape for imported .NET agents.

Example:

```json
{
  "manifestVersion": "1.1",
  "id": "com.example.crm-digest",
  "name": "CRM Digest Agent",
  "version": "0.1.0",
  "publisher": {
    "id": "com.example",
    "name": "Example"
  },
  "runtime": {
    "type": "dotnet-project",
    "projectPath": "src/Example.CrmDigest.Agent/Example.CrmDigest.Agent.csproj",
    "targetFramework": "net9.0",
    "supportsMultipleInstallations": true,
    "maximumConcurrentJobs": 1,
    "defaultActivationMode": "scheduled"
  },
  "protocol": {
    "minimumVersion": "1.0",
    "maximumVersion": "1.x"
  },
  "capabilities": [
    "crm.digest.run.v1"
  ],
  "requestedSubscriptions": [
    "com.csweet.runtime.tick.v1"
  ],
  "requestedPublications": [
    "com.csweet.runtime.completed.v1",
    "com.csweet.agent.progress.updated.v1"
  ],
  "requestedPermissions": [
    "capability.request"
  ],
  "requestedNetworkAccess": []
}
```

Do not execute repository code while previewing this manifest.

## Build model for .NET GitHub agents

Building a .NET project is code execution because MSBuild targets and package restore behavior can
run untrusted logic. Treat build as a sandboxed operation.

Build steps:

1. Resolve repository URL to commit SHA.
2. Clone that exact commit into an import workspace.
3. Validate root `csweet-agent.json`.
4. User approves import and build.
5. Start a locked-down builder container.
6. Mount only the cloned source as read-only and a disposable build output directory as writable.
7. Run `dotnet restore` and `dotnet publish` inside the builder.
8. Produce a minimal runtime image or package.
9. Compute package/image digest.
10. Store package version as immutable.

Initial build restrictions:

- No secrets in the builder.
- No host Docker socket in the builder.
- No write access to the application source tree.
- Network only to approved package feeds.
- CPU, memory, process, wall-clock, and log limits.
- Build artifacts stored under the agent package cache.

## Scheduler model

Persist schedules. Do not rely on in-memory timers as the source of truth.

Suggested fields:

- `AgentInstallationId`
- `ActivationMode`: `AlwaysOn`, `OnDemand`, `Scheduled`
- `TickFrequencySeconds`
- `NextTickAt`
- `LastTickAt`
- `LastCompletedAt`
- `MaxRuntimeSeconds`
- `MaxRetriesPerTick`
- `OverlapPolicy`: `Skip`, `Queue`, `CancelPrevious`
- `IsEnabled`

Default overlap policy should be `Skip`: if a prior tick is still running, record a skipped tick
and do not start a second container.

## Runtime instance lifecycle

```text
Queued
  -> Starting
  -> WaitingForBrokerRegistration
  -> Running
  -> CompletionReported
  -> Stopping
  -> Completed
```

Failure states:

```text
StartFailed
BrokerRegistrationTimedOut
RuntimeTimedOut
ExitedWithoutCompletion
Failed
Cancelled
PolicyDenied
```

The runtime manager should record every transition with timestamps and a short reason.

## Completion contract

The agent should publish a completion event through the broker before exiting.

Suggested event:

```text
com.csweet.runtime.completed.v1
```

Suggested payload:

```json
{
  "installationId": "agent-installation-id",
  "runtimeInstanceId": "runtime-instance-id",
  "tickId": "tick-id",
  "succeeded": true,
  "summary": "Checked CRM records and created 3 follow-up task proposals.",
  "artifacts": [],
  "issues": [],
  "nextSuggestedRunAt": null
}
```

The runtime manager should also handle process exit. If the process exits successfully without a
completion event, mark the instance as `ExitedWithoutCompletion` unless the install policy allows
exit-only completion.

## Container launch contract

Each runtime container receives:

- Broker endpoint.
- Agent ID.
- Installation ID.
- Business ID.
- Runtime instance ID.
- Tick ID.
- Workload token.
- Manifest path.

It does not receive:

- Database connection strings.
- LLM provider API keys.
- User API keys.
- Docker socket.
- Host filesystem mounts outside its workspace.

## Resource controls

Apply limits from the narrowest applicable source:

1. Platform hard maximum.
2. Global runtime configuration.
3. Business policy.
4. Agent installation grant.
5. Manifest request.

Example: if the manifest requests 4 GB memory but the global default max is 512 MB, the runtime
instance gets at most 512 MB unless an admin explicitly raises the install grant within platform
hard limits.

## Data model additions

Suggested entities:

- `AgentRuntimeGlobalSettings`
- `AgentPackageSource`
- `AgentPackageVersion`
- `AgentInstallation`
- `AgentInstallationGrant`
- `AgentSchedule`
- `AgentRuntimeInstance`
- `AgentRuntimeEvent`
- `AgentBuildJob`

Reuse names from the GitHub import docs where possible. If names already exist by the time a
developer starts this plan, extend the existing entities instead of duplicating them.

## Services

Suggested application services:

- `IAgentImportService`
- `IAgentBuildService`
- `IAgentInstallationService`
- `IAgentRuntimeSettingsService`
- `IAgentScheduleService`
- `IAgentRuntimeManager`
- `IAgentContainerRunner`

Suggested hosted services:

- `AgentScheduleWorker`
- `AgentRuntimeReconcilerWorker`
- `AgentRuntimeCleanupWorker`

## Safety rules

- Previewing an import must not execute repository code.
- Building imported source must happen in a builder sandbox.
- Running imported agents must happen in a runtime sandbox.
- Imported agents must communicate through the broker.
- Tool, MCP, API, and secret use must be mediated by platform adapters.
- Tick frequency must have a global minimum to prevent accidental container storms.
- Per-business and global concurrency limits must be enforced before starting containers.
