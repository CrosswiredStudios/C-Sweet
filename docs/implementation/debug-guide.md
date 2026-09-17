# Debug & Local Development Guide

## Prerequisites

- Windows with the .NET 10 SDK specified by `global.json`.
- Docker Desktop with its Linux container engine running. AppHost provisions PostgreSQL through Docker, so the complete application cannot start when the Docker engine is unavailable.
- An OpenAI-compatible model endpoint for AI features.
- Windows Professional, Enterprise, or Education with hardware virtualization for local untrusted-agent execution. The browser onboarding flow guides Hyper-V and RuntimeHost preparation.

Docker runs trusted development infrastructure. It is not the isolation boundary for imported or marketplace agents; those agents remain disabled until the certified hardware-isolation provider is ready.

## Quick Start

### Option 1: Aspire AppHost (Recommended)

For the most guided Windows experience, double-click `Start-CSweet.cmd` in the repository root. It checks the .NET SDK and Docker engine, attempts to start Docker Desktop when it is installed but stopped, waits for the engine, and then starts AppHost.

Run `CSweet.AppHost` to start all services together with the Aspire dashboard.

**In VS Code:**
1. Set startup project: Right-click `src/CSweet.AppHost` → "Set as Startup Project" (or use `.vscode/launch.json`)
2. Press F5 or click Run → Start Debugging

**From terminal:**
```powershell
dotnet run --project src/CSweet.AppHost
```

This will:
- Build and start `CSweet.Api` on a random port
- Build and start `CSweet.App` (Blazor frontend) on a random port
- Build and start `CSweet.WorkerHost` as a background service
- Build and start `CSweet.AgentHost` as an unprivileged project process that applies policy and brokers approved agent operations
- Provision PostgreSQL as trusted infrastructure through Docker Desktop
- Open the Aspire dashboard automatically (shows all services, health status, logs)

The Aspire dashboard URL appears in the console output (typically `https://localhost:15887`).
Docker Desktop must be running because PostgreSQL is a required AppHost resource. AgentHost no longer launches untrusted agents as Docker containers. On Windows, untrusted execution uses the separately installed RuntimeHost service and a certified Hyper-V guest.

### Option 2: Individual Projects

Run any project independently for focused debugging.

```powershell
# API only (health endpoint on default port)
dotnet run --project src/CSweet.Api

# Blazor frontend only
dotnet run --project src/CSweet.App

# Worker background service only
dotnet run --project src/CSweet.WorkerHost
```

## External Services and Host Features

| Capability | Requirement | Notes |
|---|---|---|
| Complete application startup | Docker Desktop and its Linux container engine | AppHost provisions the required PostgreSQL database as a container |
| AI features | OpenAI-compatible model endpoint | LM Studio, Ollama, vLLM, or a compatible hosted provider |
| Local untrusted agents on Windows | Hyper-V, RuntimeHost, signed guest image, and current certification | Prepared through the guided Agent Isolation onboarding flow; never replaced by Docker |

## Inference request size configuration

`CSweet.AgentHost` reads `CSweet:Llm:Queue:MaximumRequestBytes` and
`CSweet:Llm:Queue:MaximumMessageCharacters`. Both default to **0**, meaning no
application-level inference byte or text-size limit. Set a positive integer to enforce a cap;
negative values fail startup validation. For example:

```json
{
  "CSweet": {
    "Llm": {
      "Queue": {
        "MaximumRequestBytes": 0,
        "MaximumMessageCharacters": 0
      }
    }
  }
}
```

Environment variable equivalents are `CSweet__Llm__Queue__MaximumRequestBytes` and
`CSweet__Llm__Queue__MaximumMessageCharacters`. Restart AgentHost after changing them.
`PlatformLlmJobService.StartAsync` and `PlatformLlmCapabilityHandler.StreamAsync` both
honor the optional byte cap, including the provider request after ticket context is added.
Configured validation failures return an MCP invalid-parameters response instead of HTTP 500.

Zero disables these inference checks; it does not change the selected model's context capacity,
message/tool-count controls, concurrency controls, or the separate MCP/Office transport bounds.
The MCP envelope still defaults to 16 MiB, with Office framing imposing its own bound.
Requests larger than those transport bounds require a separate transport configuration/design change.

## Verifying Your Setup

### Health Endpoints

After starting the API project, verify it's running:

```powershell
# Check custom health endpoint
curl http://localhost:<port>/api/health

# Expected response:
# {"status":"ok","service":"CSweet.Api"}

# Check built-in health check (from ServiceDefaults)
curl http://localhost:<port>/health
```

### Blazor App

After starting the App project, open the URL shown in the console (typically `https://localhost:<port>`) and verify:
- C-Sweet branding is visible
- Environment label shows "Development" or "Production"
- API connectivity badge shows Connected/Disconnected based on `/api/health` availability

## VS Code Launch Configuration

For a complete debug experience, create `.vscode/launch.json`:

```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": ".NET Core Attach (AppHost)",
            "type": "coreclr",
            "request": "attach",
            "processId": "${command:pickRemoteProcess}"
        },
        {
            "name": ".NET Core Launch (CSweet.Api)",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build",
            "program": "${workspaceFolder}/src/CSweet.Api/bin/Debug/net10.0/CSweet.Api.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/CSweet.Api",
            "console": "internalConsole"
        },
        {
            "name": ".NET Core Launch (CSweet.App)",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build",
            "program": "${workspaceFolder}/src/CSweet.App/bin/Debug/net10.0/CSweet.App.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/CSweet.App",
            "console": "internalConsole"
        }
    ]
}
```

## Troubleshooting

### A Chat Turn Failed With a Generic Message

Communications shows "The agent couldn't complete that request. Please try again." when a durable chat
turn fails. The cause is persisted on the turn row, its trace events, and (for agent-side failures)
the agent work item. Follow [Chat turn diagnostics](./chat-turn-diagnostics.md) before retrying.

### Docker Engine Is Unavailable

Run:

```powershell
docker info
```

The command must succeed before AppHost starts. If it does not:

1. Open Docker Desktop.
2. Ensure Docker Desktop is using Linux containers.
3. Wait until Docker Desktop reports that the engine is running.
4. Run `Start-CSweet.cmd` or start AppHost again.

If `docker` is not recognized, install Docker Desktop using the link in the root README, then reopen the terminal so its PATH is refreshed.

### Aspire Postgres Authentication

Aspire uses Postgres credentials from `src/CSweet.AppHost/appsettings.Development.json`:

```text
CSweet:Postgres:UserName
CSweet:Postgres:Password
CSweet:Postgres:Database
```

If Postgres logs `password authentication failed` after credential changes, the existing Docker volume was likely initialized with older credentials. Delete the Aspire development volume once:

```powershell
docker volume rm csweet-aspire-postgres
```

After that, rerun AppHost. The volume should not need to be deleted again unless the configured credentials change.

### Port Already in Use
Aspire assigns random ports by default. If you need fixed ports, update the AppHost `Program.cs` with `.WithExternalHttpPorts()`.

### Aspire Dashboard Not Opening
The dashboard URL is printed to the console. Look for a line like:
```
Now listening on: https://localhost:15887
```

### "Unable to Connect" in Blazor App
When running `CSweet.App` standalone (without AppHost), the app tries to call `/api/health` relative to its own base URI. Since no API is running there, it shows "Disconnected". This is expected — run via AppHost for full connectivity.

## Local debug build and update

With the Windows development launcher and Office bootstrap configured for the current host,
ExecutionFleet offers **Build and update (debug)** even when the installed version matches the
source version. Remote Offices continue to use published updates. The action opens the existing
LocalOfficeUpgrade workflow, preserving drain, zero-active-work, UAC, certification, and
identity-preserving installation checks.

ExecutionFleetService.LaunchLocalSetupSessionAsync claims the launch in the database before starting
an elevated process. Repeated requests for that session return its current state; a failed process
start releases the launch claim. An interrupted server cannot launch the same handoff again merely
because the browser retries.

Initialize-CSweetWindowsIsolationTest.ps1 uses CSweet.DevelopmentBuild.ps1 to serialize local builds
across PowerShell and the guided launcher. It waits for live developer-bootstrap progress owners
from older, already-running scripts as well as the shared mutex. It never stops another build.
If an older build completes, its fingerprint-ready image can be reused; certification may run
again for the new operation. Unreadable progress fails closed, and waiting is bounded at two hours.
Only completed builds return PayloadResultPath; Start-CSweetDevelopmentOfficeSetup.ps1 consumes
that exact result instead of selecting the newest directory.

Verification: scripts/tests/Test-DevelopmentBuildCoordination.ps1 in CSweet.Office covers competing
processes, a legacy progress owner, completed records, and waiting peers. ExecutionFleetServiceTests
covers repeated launch requests preserving the original approval timestamp and session.

## Blocked personal epic fails to requeue

If moving a personal epic from Blocked to To Do reports
"Collection was modified; enumeration operation may not execute", inspect
`AuditPayloadSanitizer.Redact`. Redacting string values replaces children in their parent
JSON array; traversal must snapshot the array before those replacements. A serialized
`WorkTask.PlanningSpecificationJson` contains acceptance-criteria arrays and triggers the
same path during automatic recovery.

`WorkItemMutationEngine.RequeueAsync` and `RecoverAgentUpdatedBlockedWorkAsync` save the
ticket transition, reopened plan children, and wake records together. A failure during
audit capture prevents that save. Updating the agent package alone cannot fix server-side
audit capture. Rebuild/restart C-Sweet (API and AgentHost) after applying the server fix.
`PersonalTodoReconciliationWorker` runs at startup and every 30 seconds; it retries eligible
development blockers whose active installation was updated after the failure.

Regression coverage: `AuditPayloadArrayTests` exercises nested arrays and serialized
planning specifications while verifying redaction; `PersonalTodoServiceTests` covers
manual epic requeue and automatic agent-update recovery in fresh tracking contexts.

## Development blockers lack actionable detail

`SoftwareDeveloperAgent.DevelopmentBlockerMessage` in the Software Developer repository
(version 1.8.5+) includes the failed step, a bounded diagnostic, and a recovery action.
Platform failures include the capability and failure code; exhausted task validation includes
the failed command. An unknown compute outcome requires checking the existing operation before
replay, and is not automatically described as a Docker build failure.

`AgentTicketFeedback.ReportedReason` preserves the multiline owner-facing blocker when creating
an `agent.failure` comment. `EmployeePersonalBoard` and agent comments in `WorkItemComments`
render those reports through `ChatMarkdown`. Rebuild/restart C-Sweet and update the Software
Developer installation to use both halves of this fix. Historical comments are not rewritten.

Regression coverage: `DeploymentDiagnosticTests`, the exhausted-budget case in
`ComputeDeploymentRecoveryTests.CompleteBacklogPrecedesCodingAndRestartAdvancesOnlyOneTaskWithFreshEvidence`,
and `AgentTicketFeedbackTests.ReportedBlockerPreservesEvidenceAndRecoveryStepsInPersistedComment`.
