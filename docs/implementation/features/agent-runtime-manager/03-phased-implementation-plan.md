# Agent Runtime Manager - Phased Implementation Plan

This plan is written for junior developers. Complete each phase in order. Each phase should build,
pass tests, and include a short manual verification note in the PR.

## Phase 0 - Runtime baseline and terminology

### Goal

Make the current brokered-agent architecture explicit in the implementation docs and prepare Docker
Compose for runtime-managed agents.

### Tasks

1. Add `CSweet.AgentHost` to Docker Compose.
2. Add the first-party Personal Assistant as a separate Compose service.
3. Confirm the API gateway can reach the broker in Compose.
4. Confirm the Personal Assistant can reach the broker in Compose.
5. Add health checks and environment variables for broker endpoint configuration.
6. Update deployment docs with the brokered agent runtime topology.

### Acceptance criteria

- `docker compose up -d` starts app, API, Postgres, broker, worker host, and Personal Assistant.
- The broker reports healthy.
- The Personal Assistant registers with the broker.
- Existing chat behavior still works in Aspire and Compose.

## Phase 1 - Global Agent Runtime settings

### Goal

Add editable global settings for agent/container behavior on the Configuration page.

### Backend tasks

1. Add `AgentRuntimeGlobalSettings` entity.
2. Add EF configuration and migration.
3. Seed conservative default settings during startup or migration.
4. Add `AgentRuntimeSettingsResponse`.
5. Add `UpdateAgentRuntimeSettingsRequest`.
6. Add `IAgentRuntimeSettingsService`.
7. Add implementation in Infrastructure.
8. Add `AgentRuntimeSettingsEndpoints`.
9. Map endpoints in `CSweet.Api/Program.cs`.
10. Add audit events for settings changes.

### UI tasks

1. Add methods to the shared UI API client.
2. Add an Agent Runtime section to `Configuration.razor`.
3. Use numeric fields for limits.
4. Use selects for activation mode and overlap policy.
5. Use toggles for imported agents and cleanup behavior.
6. Show save success/failure messages.

### Tests

- Unit tests for validation rules.
- Integration tests for GET/PUT settings.
- Manual UI check that values load, save, and reload.

### Acceptance criteria

- Admin can edit global agent runtime settings from `/configuration`.
- Invalid values are rejected.
- Settings persist across application restart.

## Phase 2 - Import preview for .NET GitHub agents

### Goal

Let the Agents page preview a public GitHub repository containing a root `csweet-agent.json`
without executing repository code.

### Backend tasks

1. Extend `AgentManifest` to support `runtime.type = "dotnet-project"`.
2. Add optional runtime fields:
   - `projectPath`
   - `targetFramework`
   - `defaultActivationMode`
3. Add `AgentPackageSource` entity.
4. Add `AgentPackageVersion` entity.
5. Add import preview DTOs:
   - `PreviewAgentImportRequest`
   - `AgentImportPreviewResponse`
   - `AgentManifestWarningResponse`
6. Implement GitHub URL normalization.
7. Resolve the default branch to a commit SHA.
8. Fetch only root `csweet-agent.json`.
9. Validate manifest fields.
10. Compute manifest digest.
11. Persist an import candidate with status `Previewed`.

### UI tasks

1. Add **Import Agent** button to `Agents.razor`.
2. Open a modal/dialog with a GitHub URL field.
3. Call preview endpoint.
4. Show manifest summary:
   - name
   - ID
   - version
   - publisher
   - runtime type
   - project path
   - capabilities
   - subscriptions
   - publications
   - requested permissions
   - requested network access
   - warnings

### Tests

- Unit tests for URL normalization.
- Unit tests for manifest validation.
- Integration test for previewing a fake public repo response through a test double.

### Acceptance criteria

- Preview never runs `dotnet restore`, `dotnet build`, scripts, or repository code.
- Unsupported manifest values produce clear errors.
- Preview records source URL, commit SHA, and manifest digest.

## Phase 3 - Approval, installation, and tick schedule

### Goal

Install an approved import with bounded grants and a periodic schedule.

### Backend tasks

1. Add `AgentInstallation` entity.
2. Add `AgentInstallationGrant` entity.
3. Add `AgentSchedule` entity.
4. Add activation mode enum:
   - `AlwaysOn`
   - `OnDemand`
   - `Scheduled`
5. Add overlap policy enum:
   - `Skip`
   - `Queue`
   - `CancelPrevious`
6. Add install DTOs:
   - `InstallAgentRequest`
   - `AgentInstallationResponse`
   - `AgentScheduleResponse`
7. Validate requested grants do not exceed manifest requests.
8. Validate tick frequency against global minimum.
9. Compute initial `NextTickAt`.
10. Add endpoints:
   - `POST /api/agents/imports/{importId}/install`
   - `GET /api/agents/installations`
   - `GET /api/agents/installations/{id}`
   - `PUT /api/agents/installations/{id}/schedule`
   - `POST /api/agents/installations/{id}/run-now`
   - `POST /api/agents/installations/{id}/disable`
11. Update broker authorization policy to read persisted install grants.

### UI tasks

1. Add install step to the import dialog.
2. Let admin choose activation mode.
3. Let admin configure tick frequency.
4. Let admin approve or remove requested capabilities/publications/subscriptions.
5. Let admin set max runtime, memory, and CPU within global limits.
6. Show installed agents on the Agents page with schedule status.

### Tests

- Unit test: grant cannot exceed manifest.
- Unit test: tick frequency below minimum is rejected.
- Integration test: install creates installation, grant, and schedule.

### Acceptance criteria

- User can install a previewed .NET agent.
- User can configure tick frequency during install.
- Broker grants come from approved installation records.

## Phase 4 - Isolated .NET build/package pipeline

### Goal

Clone and build the approved .NET project in an isolated builder workflow.

### Backend tasks

1. Add `AgentBuildJob` entity.
2. Add build statuses:
   - `Queued`
   - `Cloning`
   - `Building`
   - `Succeeded`
   - `Failed`
   - `Cancelled`
3. Add `IAgentBuildService`.
4. Add `AgentBuildWorker` hosted service.
5. Clone the approved commit into an application-owned import workspace.
6. Run restore/publish inside a builder container.
7. Enforce build timeout, memory, CPU, log, and workspace limits.
8. Store build logs with retention.
9. Produce an immutable package directory or runtime image.
10. Compute package digest.
11. Mark package version `Built`.

### Important implementation note

Do not use the host machine's normal shell to run `dotnet build` against imported source. MSBuild
can execute untrusted logic. The build must happen in an isolated builder environment.

### Tests

- Unit tests for build job state transitions.
- Integration/manual test with a tiny sample .NET agent repository.
- Manual failure test with a broken project path.

### Acceptance criteria

- Approved .NET agent source is cloned at the recorded commit SHA.
- Build runs in an isolated builder.
- Build output has a stable digest.
- Failed builds do not leave active containers behind.

## Phase 5 - Container runner abstraction

### Goal

Add a container runner abstraction used by the runtime manager.

### Backend tasks

1. Add `IAgentContainerRunner`.
2. Add methods:
   - `StartAsync`
   - `StopAsync`
   - `InspectAsync`
   - `RemoveAsync`
   - `GetLogsAsync`
3. Add `AgentContainerStartRequest`.
4. Add `AgentContainerStatus`.
5. Implement Docker-based runner for local/Compose environments.
6. Apply limits from global settings and installation grant.
7. Pass only approved runtime environment variables.
8. Deny privileged mode and host Docker socket mounts.
9. Add structured logs for container start/stop/failure.

### Tests

- Unit tests using a fake `IAgentContainerRunner`.
- Manual Docker test with a sample image.

### Acceptance criteria

- Runtime manager can start and stop a built agent container through an interface.
- Container start request includes resource limits.
- No secrets or database connection strings are passed to the container.

## Phase 6 - Runtime Manager and scheduler

### Goal

Start periodic ephemeral containers on their configured tick frequency.

### Backend tasks

1. Add `AgentRuntimeInstance` entity.
2. Add `AgentRuntimeEvent` entity.
3. Add runtime instance states from the architecture doc.
4. Add `IAgentRuntimeManager`.
5. Add `AgentScheduleWorker` hosted service.
6. Add due-schedule query with row claiming to avoid duplicate starts.
7. Enforce global, business, and installation concurrency limits.
8. Apply overlap policy.
9. Start container through `IAgentContainerRunner`.
10. Wait for broker registration up to configured timeout.
11. Mark runtime instance `Running`.
12. Stop instance when completion event arrives.
13. Stop instance on max runtime timeout.
14. Compute and persist next tick.

### Broker tasks

1. Add runtime instance ID and tick ID to registration context or workload token claims.
2. Add support for completion event `com.csweet.runtime.completed.v1`.
3. Validate completion event publisher matches the runtime instance installation.
4. Notify runtime manager or persist event for reconciler polling.

### Tests

- Unit test: due scheduled activation starts one runtime instance.
- Unit test: running previous tick with `Skip` prevents overlap.
- Unit test: concurrency limit prevents container start.
- Unit test: completion event transitions instance to completed.
- Unit test: timeout transitions instance to runtime timed out.

### Acceptance criteria

- Scheduled agent starts at tick frequency.
- Agent can report completion through broker.
- Container is stopped after completion.
- Next tick is scheduled after completion.

## Phase 7 - Agents page management

### Goal

Let users manage imported agents and schedules from the Agents page.

### UI tasks

1. Show first-party and imported agents in a unified list.
2. Add status badges:
   - Installed
   - Build queued
   - Build failed
   - Scheduled
   - Running
   - Disabled
3. Add schedule summary:
   - activation mode
   - tick frequency
   - next tick
   - last run result
4. Add actions:
   - Import Agent
   - Run Now
   - Enable/Disable
   - Edit Schedule
   - View Runs
   - View Build Log
5. Keep existing per-agent configuration behavior for agents that expose configuration schema.

### Acceptance criteria

- User can import from Agents page.
- User can configure tick frequency.
- User can see next run and last run.
- User can run a scheduled agent manually.
- User can disable an installation.

## Phase 8 - Cleanup, observability, and hardening

### Goal

Make the runtime manager safe to leave running.

### Tasks

1. Add `AgentRuntimeCleanupWorker`.
2. Remove completed containers based on global settings.
3. Remove old workspaces based on global settings.
4. Keep failed build/runtime logs according to retention settings.
5. Add dashboard-friendly runtime metrics.
6. Add audit events for:
   - import preview
   - install approval
   - build start/success/failure
   - schedule tick
   - container start/stop
   - completion
   - timeout
   - policy denial
7. Add rate limits for import preview, build, run-now, and schedule ticks.

### Acceptance criteria

- Completed containers do not accumulate forever.
- Runtime history remains visible.
- Failures are diagnosable from UI and logs.
- Policy denials are clear and auditable.
